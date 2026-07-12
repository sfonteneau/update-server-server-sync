// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Source;
using Microsoft.PackageGraph.Storage;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    partial class MetadataSync
    {
        /// <summary>
        /// Runs the observed-product and observed-driver synchronization phases.
        /// Client scans only record observations; every upstream request happens here.
        /// </summary>
        public static void FetchObserved(FetchObservedOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.SkipProducts && options.SkipDrivers)
            {
                throw new InvalidOperationException(
                    "Both --skip-products and --skip-drivers were selected; there is nothing to fetch.");
            }

            var store = MetadataStoreCreator.CreateFromOptions(options as IMetadataStoreOptions);
            if (store == null)
            {
                return;
            }

            using (store)
            {
                var operationsStore = store as IObservedOperationsStore;
                var publicationControl = store as IMetadataCatalogPublicationControl;
                if (publicationControl == null)
                {
                    throw new InvalidOperationException(
                        "fetch-observed requires a metadata store that can defer catalog publication.");
                }

                publicationControl.DeferCatalogPublication();
                var packageCountBefore = (long)store.GetPackageIdentities().Count;
                long runId = 0;
                try
                {
                    runId = operationsStore?.StartObservedFetchRun(
                        DateTimeOffset.UtcNow,
                        options.SeenWithinDays,
                        !options.SkipProducts,
                        !options.SkipDrivers,
                        options.IncludeCompatibleIds,
                        options.DryRun,
                        packageCountBefore) ?? 0;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError(
                        $"Cannot persist fetch-observed start state: {ex}");
                }

                var failures = new List<Exception>();
                var completedPhases = new List<string>();
                var runWasFinalized = false;

                try
                {
                    if (!options.SkipProducts)
                    {
                        try
                        {
                            FetchObservedProducts(options, store);
                            completedPhases.Add("products");
                        }
                        catch (Exception ex)
                        {
                            failures.Add(new InvalidOperationException("Observed product synchronization failed.", ex));
                            ConsoleOutput.WriteRed($"Observed product synchronization failed: {ex.Message}");
                        }
                    }

                    if (!options.SkipDrivers)
                    {
                        try
                        {
                            FetchObservedDrivers(options, store);
                            completedPhases.Add("drivers");
                        }
                        catch (Exception ex)
                        {
                            failures.Add(new InvalidOperationException("Observed driver synchronization failed.", ex));
                            ConsoleOutput.WriteRed($"Observed driver synchronization failed: {ex.Message}");
                        }
                    }

                    // Make package/index changes durable before recording the final
                    // package count. While fetch-observed is running, Flush keeps
                    // the catalog generation unpublished so a checkpoint can be
                    // resumed without exposing a partial graph to Windows clients.
                    store.Flush();

                    if (failures.Count == 0 && operationsStore != null)
                    {
                        var operationalStatus = operationsStore.GetObservedSyncOperationalStatus();
                        if (operationalStatus.PendingCheckpointCount > 0)
                        {
                            failures.Add(new InvalidOperationException(
                                $"Catalog publication remains deferred because " +
                                $"{operationalStatus.PendingCheckpointCount} metadata checkpoint(s) " +
                                "are still incomplete."));
                        }
                    }

                    if (failures.Count == 0)
                    {
                        publicationControl.PublishDeferredCatalogChanges();
                    }

                    var packageCountAfter = (long)store.GetPackageIdentities().Count;
                    var status = failures.Count > 0
                        ? completedPhases.Count > 0 || packageCountAfter > packageCountBefore
                            ? ObservedFetchRunStatus.Partial
                            : ObservedFetchRunStatus.Failed
                        : options.DryRun
                            ? ObservedFetchRunStatus.DryRun
                            : ObservedFetchRunStatus.Succeeded;
                    var summary = completedPhases.Count == 0
                        ? "No phase completed."
                        : "Completed phase(s): " + string.Join(", ", completedPhases) + ".";
                    var error = failures.Count == 0
                        ? null
                        : string.Join(
                            Environment.NewLine,
                            failures.Select(failure => failure.GetBaseException().Message));

                    runWasFinalized = TryCompleteObservedFetchRun(
                        operationsStore,
                        runId,
                        status,
                        packageCountAfter,
                        summary,
                        error);

                    if (failures.Count > 0)
                    {
                        throw new AggregateException(
                            $"fetch-observed completed with {failures.Count} failed phase(s).",
                            failures);
                    }
                }
                catch (Exception ex)
                {
                    if (!runWasFinalized)
                    {
                        TryCompleteObservedFetchRun(
                            operationsStore,
                            runId,
                            ObservedFetchRunStatus.Failed,
                            store.GetPackageIdentities().Count,
                            "fetch-observed stopped before normal completion.",
                            ex.GetBaseException().Message);
                    }

                    throw;
                }
            }
        }

        private static bool TryCompleteObservedFetchRun(
            IObservedOperationsStore operationsStore,
            long runId,
            ObservedFetchRunStatus status,
            long packageCountAfter,
            string summary,
            string error)
        {
            if (operationsStore == null || runId <= 0)
            {
                return true;
            }

            try
            {
                operationsStore.CompleteObservedFetchRun(
                    runId,
                    DateTimeOffset.UtcNow,
                    status,
                    packageCountAfter,
                    summary,
                    error);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    $"Cannot persist fetch-observed completion state: {ex}");
                return false;
            }
        }

        private static void FetchObservedDrivers(
            FetchObservedOptions options,
            IMetadataStore store)
        {
            if (!string.Equals(
                options.EndpointType,
                FetchPackagesOptions.MicrosoftUpdateEndpoint,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "Observed driver fetch currently supports only --endpoint-type microsoft-update.");
            }

            if (options.SeenWithinDays < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options.SeenWithinDays),
                    "--seen-within-days must be zero or greater.");
            }

                if (store is not IObservedInventoryStore observedInventoryStore)
                {
                    throw new InvalidOperationException(
                        "Observed driver fetch requires a local SQLite store with observed inventory support.");
                }

                if (store is not IDriverSyncStateStore driverStateStore
                    || store is not ISyncCheckpointStore checkpointStore)
                {
                    throw new InvalidOperationException(
                        "Observed driver fetch requires per-identifier driver anchors and resumable checkpoints.");
                }

                var upstreamEndpoint = string.IsNullOrEmpty(options.UpstreamEndpoint)
                    ? MicrosoftUpdate.Source.Endpoint.Default
                    : new MicrosoftUpdate.Source.Endpoint(options.UpstreamEndpoint);
                var seenSince = options.SeenWithinDays > 0
                    ? DateTimeOffset.UtcNow.AddDays(-options.SeenWithinDays)
                    : (DateTimeOffset?)null;
                var languageFilter = CreateLanguageFilter(options.LanguageFilter);
                var stripUnrequestedLocalizedProperties =
                    languageFilter.Count > 0 && !options.KeepAllLocalizedProperties;
                var metadataFilter = new UpstreamSourceFilter(
                    Array.Empty<Guid>(),
                    Array.Empty<Guid>(),
                    languageFilter,
                    stripUnrequestedLocalizedProperties);
                var scopeKey = BuildDriverSyncScopeKey(
                    upstreamEndpoint,
                    languageFilter,
                    stripUnrequestedLocalizedProperties);

                var pnpObservations = observedInventoryStore
                    .GetObservedPnpHardwareIds(seenSince)
                    .Select(observation => observation.Identifier)
                    .ToList();
                var compatibleObservations = options.IncludeCompatibleIds
                    ? observedInventoryStore
                        .GetObservedCompatibleIds(seenSince)
                        .Select(observation => observation.Identifier)
                        .ToList()
                    : new List<string>();
                var computerObservations = observedInventoryStore
                    .GetObservedComputerIds(seenSince)
                    .Select(observation => observation.ComputerId)
                    .ToList();

                var identifiers = pnpObservations
                    .Concat(compatibleObservations)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(DriverSyncIdentifier.Pnp)
                    .Concat(computerObservations
                        .Where(value => value != Guid.Empty)
                        .Select(DriverSyncIdentifier.Computer))
                    .Distinct()
                    .OrderBy(identifier => (int)identifier.Type)
                    .ThenBy(identifier => identifier.Value, StringComparer.Ordinal)
                    .ToList();

                var pendingCheckpoints = driverStateStore
                    .GetDriverSyncCheckpoints(scopeKey)
                    .ToList();
                var identifierStates = driverStateStore
                    .GetDriverSyncIdentifierStates(scopeKey, identifiers)
                    .ToDictionary(state => state.Identifier);
                var fullIdentifierCount = options.IgnoreSyncAnchor
                    ? identifiers.Count
                    : identifiers.Count(identifier => !identifierStates.ContainsKey(identifier));
                var deltaIdentifierCount = options.IgnoreSyncAnchor
                    ? 0
                    : identifiers.Count - fullIdentifierCount;

                Console.WriteLine();
                Console.WriteLine("Observed driver fetch");
                Console.WriteLine("=====================");
                Console.WriteLine(seenSince.HasValue
                    ? $"Observation window : last {options.SeenWithinDays} day(s)"
                    : "Observation window : all observations");
                Console.WriteLine(
                    $"Hardware IDs       : {pnpObservations.Count} PnP" +
                    (options.IncludeCompatibleIds
                        ? $", {compatibleObservations.Count} compatible"
                        : ", compatible IDs disabled") +
                    $", {computerObservations.Count} computer ID(s)");
                Console.WriteLine(
                    $"Unique identifiers : {identifiers.Count}; " +
                    $"full={fullIdentifierCount}; delta={deltaIdentifierCount}; " +
                    $"pending checkpoints={pendingCheckpoints.Count}");
                Console.WriteLine(
                    $"Metadata languages : {(languageFilter.Count == 0 ? "all" : string.Join(",", languageFilter))}");

                if (options.DryRun)
                {
                    Console.WriteLine();
                    Console.WriteLine("Dry run: no upstream driver request will be sent.");
                    foreach (var checkpoint in pendingCheckpoints)
                    {
                        Console.WriteLine(
                            $"PENDING {checkpoint.Checkpoint.AnchorKey}  " +
                            $"members={checkpoint.Identifiers.Count}  " +
                            $"progress={checkpoint.Checkpoint.CompletedItems}/{checkpoint.Checkpoint.TotalItems}");
                    }

                    foreach (var identifier in identifiers.Take(50))
                    {
                        var mode = !options.IgnoreSyncAnchor
                            && identifierStates.TryGetValue(identifier, out var state)
                            && !string.IsNullOrWhiteSpace(state.Anchor)
                                ? "DELTA"
                                : "FULL";
                        Console.WriteLine($"{mode,-5} {FormatDriverIdentifier(identifier)}");
                    }

                    if (identifiers.Count > 50)
                    {
                        Console.WriteLine($"... {identifiers.Count - 50} additional identifier(s)");
                    }

                    return;
                }

                var batchSize = Math.Max(1, options.MetadataBatchSize);
                var cancellationTokenSource = new CancellationTokenSource();
                var failures = new List<Exception>();
                var blockedIdentifiers = new HashSet<DriverSyncIdentifier>();

                if (options.ResetSyncCheckpoint || options.IgnoreSyncAnchor)
                {
                    foreach (var checkpoint in pendingCheckpoints)
                    {
                        driverStateStore.ClearDriverSyncCheckpoint(checkpoint.Checkpoint.AnchorKey);
                        Console.WriteLine(
                            $"Discarded unfinished driver checkpoint " +
                            $"({checkpoint.Checkpoint.CompletedItems}/{checkpoint.Checkpoint.TotalItems} revisions completed, " +
                            $"{checkpoint.Identifiers.Count} identifier(s)).");
                    }

                    pendingCheckpoints.Clear();
                }
                else
                {
                    foreach (var checkpoint in pendingCheckpoints)
                    {
                        try
                        {
                            Console.WriteLine();
                            Console.WriteLine(
                                $"Resuming driver checkpoint for {checkpoint.Identifiers.Count} identifier(s): " +
                                $"{checkpoint.Checkpoint.CompletedItems}/{checkpoint.Checkpoint.TotalItems} revision(s) stored. " +
                                "GetDriverIdList will not be called again.");
                            var resumeSource = CreateDriverSource(
                                upstreamEndpoint,
                                CreateDriverUpdateFilter(checkpoint.Identifiers),
                                metadataFilter,
                                checkpoint.Checkpoint.AnchorFrom,
                                batchSize);
                            ResumeDriverSyncCheckpoint(
                                store,
                                checkpointStore,
                                driverStateStore,
                                resumeSource,
                                checkpoint.Checkpoint.AnchorKey,
                                batchSize,
                                cancellationTokenSource.Token);
                        }
                        catch (Exception ex)
                        {
                            foreach (var identifier in checkpoint.Identifiers)
                            {
                                blockedIdentifiers.Add(identifier);
                            }

                            failures.Add(new InvalidOperationException(
                                $"Could not resume driver checkpoint {checkpoint.Checkpoint.AnchorKey}.",
                                ex));
                            ConsoleOutput.WriteRed(
                                $"Driver checkpoint resume failed; it was retained: {ex.Message}");
                        }
                    }
                }

                var identifiersToQuery = identifiers
                    .Where(identifier => !blockedIdentifiers.Contains(identifier))
                    .ToList();
                if (identifiersToQuery.Count == 0)
                {
                    if (failures.Count > 0)
                    {
                        throw new AggregateException(
                            "No additional driver identifier could be synchronized because unfinished checkpoints failed to resume.",
                            failures);
                    }

                    Console.WriteLine("No observed hardware identifier is active in the selected time window. Nothing to fetch.");
                    return;
                }

                // A completed resumed checkpoint can have changed identifier anchors.
                identifierStates = options.IgnoreSyncAnchor
                    ? new Dictionary<DriverSyncIdentifier, DriverSyncIdentifierState>()
                    : driverStateStore
                        .GetDriverSyncIdentifierStates(scopeKey, identifiersToQuery)
                        .ToDictionary(state => state.Identifier);

                var groups = new Dictionary<string, List<DriverSyncIdentifier>>(StringComparer.Ordinal);
                foreach (var identifier in identifiersToQuery)
                {
                    string stableAnchor = null;
                    if (!options.IgnoreSyncAnchor
                        && identifierStates.TryGetValue(identifier, out var state)
                        && !string.IsNullOrWhiteSpace(state.Anchor))
                    {
                        stableAnchor = state.Anchor;
                    }

                    var groupKey = stableAnchor ?? string.Empty;
                    if (!groups.TryGetValue(groupKey, out var members))
                    {
                        members = new List<DriverSyncIdentifier>();
                        groups.Add(groupKey, members);
                    }

                    members.Add(identifier);
                }

                Console.WriteLine(
                    $"Driver request groups: {groups.Count} " +
                    "(identifiers sharing an anchor are sent together; service limits are applied automatically).");

                var groupNumber = 0;
                foreach (var group in groups
                    .OrderBy(pair => string.IsNullOrEmpty(pair.Key) ? 0 : 1)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    groupNumber++;
                    var stableAnchor = string.IsNullOrEmpty(group.Key) ? null : group.Key;
                    var members = group.Value
                        .OrderBy(identifier => (int)identifier.Type)
                        .ThenBy(identifier => identifier.Value, StringComparer.Ordinal)
                        .ToList();
                    var computerCount = members.Count(
                        identifier => identifier.Type == DriverSyncIdentifierType.ComputerHardwareId);
                    var pnpCount = members.Count - computerCount;
                    var checkpointAnchorKey = BuildDriverCheckpointAnchorKey(
                        scopeKey,
                        stableAnchor,
                        members);

                    Console.WriteLine();
                    Console.WriteLine(
                        $"[{groupNumber}/{groups.Count}] " +
                        $"{(stableAnchor == null ? "FULL" : "DELTA")} driver group: " +
                        $"{pnpCount} PnP/compatible, {computerCount} computer identifier(s)");

                    try
                    {
                        FetchMicrosoftDriverRevisions(
                            store,
                            checkpointStore,
                            driverStateStore,
                            upstreamEndpoint,
                            metadataFilter,
                            scopeKey,
                            checkpointAnchorKey,
                            stableAnchor,
                            members,
                            batchSize,
                            cancellationTokenSource.Token);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new InvalidOperationException(
                            $"Driver synchronization failed for group {groupNumber}/{groups.Count}.",
                            ex));
                        ConsoleOutput.WriteRed(
                            $"Driver group failed; its stable per-identifier anchors/checkpoint were preserved: {ex.Message}");
                    }
                }

                Console.WriteLine();
                if (failures.Count > 0)
                {
                    throw new AggregateException(
                        $"Observed driver fetch completed with {failures.Count} failed group(s).",
                        failures);
                }

                ConsoleOutput.WriteGreen(
                    $"Done. {identifiersToQuery.Count} observed driver identifier(s) synchronized in {groups.Count} group(s).");
        }

        private static void FetchMicrosoftDriverRevisions(
            IMetadataStore store,
            ISyncCheckpointStore checkpointStore,
            IDriverSyncStateStore driverStateStore,
            MicrosoftUpdate.Source.Endpoint upstreamEndpoint,
            UpstreamSourceFilter metadataFilter,
            string scopeKey,
            string checkpointAnchorKey,
            string stableAnchor,
            IReadOnlyList<DriverSyncIdentifier> members,
            int batchSize,
            CancellationToken cancellationToken)
        {
            var driverSource = CreateDriverSource(
                upstreamEndpoint,
                CreateDriverUpdateFilter(members),
                metadataFilter,
                stableAnchor,
                batchSize);

            Console.WriteLine("Getting matching driver revision IDs ...");
            var revisionIdentities = driverSource.GetPackageIdentities();
            if (string.IsNullOrWhiteSpace(driverSource.NewAnchor))
            {
                throw new InvalidOperationException(
                    "The upstream server returned a driver revision list without a new anchor. " +
                    "The per-identifier state cannot be advanced safely.");
            }

            driverStateStore.CreateDriverSyncCheckpoint(
                checkpointAnchorKey,
                scopeKey,
                stableAnchor,
                driverSource.NewAnchor,
                revisionIdentities,
                members);

            if (!checkpointStore.TryGetSyncCheckpoint(checkpointAnchorKey, out var createdCheckpoint))
            {
                throw new InvalidOperationException(
                    "The driver synchronization checkpoint was created but could not be read back.");
            }

            Console.WriteLine(
                $"Microsoft returned {revisionIdentities.Count} matching driver revision(s) " +
                $"and {driverSource.DriverSetCount} driver set(s). " +
                $"The checkpoint contains {createdCheckpoint.TotalItems} revision(s) not already stored. " +
                "Identifier anchors remain unchanged until completion.");

            ResumeDriverSyncCheckpoint(
                store,
                checkpointStore,
                driverStateStore,
                driverSource,
                checkpointAnchorKey,
                batchSize,
                cancellationToken);
        }

        private static UpstreamUpdatesSource CreateDriverSource(
            MicrosoftUpdate.Source.Endpoint upstreamEndpoint,
            DriverUpdateFilter driverFilter,
            UpstreamSourceFilter metadataFilter,
            string oldAnchor,
            int batchSize)
        {
            var source = new UpstreamUpdatesSource(
                upstreamEndpoint,
                metadataFilter,
                oldAnchor,
                driverFilter)
            {
                BatchSize = Math.Max(1, batchSize)
            };
            source.MetadataCopyProgress += Program.OnPackageCopyProgress;
            return source;
        }

        private static DriverUpdateFilter CreateDriverUpdateFilter(
            IEnumerable<DriverSyncIdentifier> identifiers)
        {
            var normalized = (identifiers ?? Array.Empty<DriverSyncIdentifier>())
                .Where(identifier => identifier != null)
                .Distinct()
                .ToList();
            return new DriverUpdateFilter(
                Array.Empty<Guid>(),
                normalized
                    .Where(identifier => identifier.Type == DriverSyncIdentifierType.ComputerHardwareId)
                    .Select(identifier => identifier.GetComputerId()),
                normalized
                    .Where(identifier => identifier.Type == DriverSyncIdentifierType.PnpHardwareId)
                    .Select(identifier => identifier.Value));
        }

        private static void ResumeDriverSyncCheckpoint(
            IMetadataStore store,
            ISyncCheckpointStore checkpointStore,
            IDriverSyncStateStore driverStateStore,
            UpstreamUpdatesSource driverSource,
            string checkpointAnchorKey,
            int batchSize,
            CancellationToken cancellationToken)
        {
            checkpointStore.ReconcileSyncCheckpoint(checkpointAnchorKey);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!checkpointStore.TryGetSyncCheckpoint(checkpointAnchorKey, out var checkpoint))
                {
                    throw new InvalidOperationException(
                        "The local driver synchronization checkpoint disappeared before completion.");
                }

                if (checkpoint.PendingItems == 0)
                {
                    driverStateStore.CompleteDriverSyncCheckpoint(checkpointAnchorKey);
                    Console.WriteLine();
                    Console.WriteLine(
                        "Driver checkpoint complete. Promoted the new anchor to every identifier atomically.");
                    return;
                }

                var pendingItems = checkpointStore
                    .GetPendingSyncCheckpointItems(checkpointAnchorKey, batchSize)
                    .ToList();
                if (pendingItems.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"The driver checkpoint reports {checkpoint.PendingItems} pending item(s), " +
                        "but no pending identities could be read.");
                }

                var pendingUpdates = pendingItems
                    .Select(identity => identity as MicrosoftUpdatePackageIdentity
                        ?? throw new InvalidOperationException(
                            $"Unsupported driver checkpoint identity: {identity}"))
                    .ToList();

                checkpointStore.MarkSyncCheckpointItemsAttempted(checkpointAnchorKey, pendingItems);
                try
                {
                    driverSource.CopyPackagesTo(
                        store,
                        pendingUpdates,
                        cancellationToken,
                        completedItems => checkpointStore.MarkSyncCheckpointItemsCompleted(
                            checkpointAnchorKey,
                            completedItems));
                }
                catch (Exception ex)
                {
                    try
                    {
                        checkpointStore.MarkSyncCheckpointItemsFailed(
                            checkpointAnchorKey,
                            pendingItems,
                            ex.Message);
                        checkpointStore.ReconcileSyncCheckpoint(checkpointAnchorKey);
                    }
                    catch
                    {
                        // Preserve the original upstream/storage exception.
                    }

                    if (checkpointStore.TryGetSyncCheckpoint(
                        checkpointAnchorKey,
                        out var failedCheckpoint))
                    {
                        Console.WriteLine();
                        ConsoleOutput.WriteRed(
                            $"Driver fetch interrupted. Checkpoint kept: " +
                            $"{failedCheckpoint.CompletedItems}/{failedCheckpoint.TotalItems} revision(s) complete. " +
                            "Rerun fetch-observed to resume.");
                    }

                    throw new InvalidOperationException(
                        "The driver checkpoint was retained and no per-identifier anchor was advanced.",
                        ex);
                }

                if (checkpointStore.TryGetSyncCheckpoint(
                    checkpointAnchorKey,
                    out var updatedCheckpoint))
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        $"Driver checkpoint progress: {updatedCheckpoint.CompletedItems}/" +
                        $"{updatedCheckpoint.TotalItems} revision(s) stored; " +
                        $"{updatedCheckpoint.PendingItems} remaining.");
                }
            }
        }

        private static string BuildDriverSyncScopeKey(
            MicrosoftUpdate.Source.Endpoint endpoint,
            IReadOnlyCollection<int> languageFilter,
            bool stripUnrequestedLocalizedProperties)
        {
            var keyData = new
            {
                Type = "microsoft-update-driver-identifiers-v1",
                Endpoint = endpoint.URI,
                Categories = Array.Empty<string>(),
                Languages = (languageFilter ?? Array.Empty<int>())
                    .OrderBy(value => value)
                    .ToList(),
                StripUnrequestedLocalizedProperties = stripUnrequestedLocalizedProperties
            };

            return "sync-scope:mu-driver:" + ComputeStableHash(keyData);
        }

        private static string BuildDriverCheckpointAnchorKey(
            string scopeKey,
            string stableAnchor,
            IEnumerable<DriverSyncIdentifier> identifiers)
        {
            var keyData = new
            {
                Type = "microsoft-update-driver-checkpoint-v1",
                Scope = scopeKey,
                AnchorFrom = stableAnchor ?? string.Empty,
                Identifiers = (identifiers ?? Array.Empty<DriverSyncIdentifier>())
                    .Where(identifier => identifier != null)
                    .OrderBy(identifier => (int)identifier.Type)
                    .ThenBy(identifier => identifier.Value, StringComparer.Ordinal)
                    .Select(identifier => new
                    {
                        Type = (int)identifier.Type,
                        identifier.Value
                    })
                    .ToList()
            };

            return "sync-checkpoint:mu-driver:" + ComputeStableHash(keyData);
        }

        private static string ComputeStableHash(object value)
        {
            var json = JsonConvert.SerializeObject(value);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(
                    sha256.ComputeHash(Encoding.UTF8.GetBytes(json)))
                .ToLowerInvariant();
        }

        private static string FormatDriverIdentifier(DriverSyncIdentifier identifier)
        {
            return identifier.Type == DriverSyncIdentifierType.ComputerHardwareId
                ? $"COMPUTER {identifier.Value}"
                : $"PNP      {identifier.Value}";
        }
    }
}
