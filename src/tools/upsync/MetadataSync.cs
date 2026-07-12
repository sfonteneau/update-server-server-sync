// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using System.Threading;
using Microsoft.PackageGraph.MicrosoftUpdate.Source;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    /// <summary>
    /// Implements operations to fetch update metadata from an upstream update server
    /// </summary>
    partial class MetadataSync
    {
        public static void FetchConfiguration(FetchConfigurationOptions options)
        {
            MicrosoftUpdate.Source.Endpoint upstreamEndpoint;
            if (!string.IsNullOrEmpty(options.UpstreamEndpoint))
            {
                upstreamEndpoint = new MicrosoftUpdate.Source.Endpoint(options.UpstreamEndpoint);
            }
            else
            {
                upstreamEndpoint = MicrosoftUpdate.Source.Endpoint.Default;
            }

            var server = new UpstreamServerClient(upstreamEndpoint);
            server.MetadataQueryProgress += Server_MetadataQueryProgress;
            var configData = server.GetServerConfigData().GetAwaiter().GetResult();

            File.WriteAllText(options.OutFile, JsonConvert.SerializeObject(configData));
        }

        public static void ReIndex(ReindexStoreOptions options)
        {
            var sourceToUpdate = MetadataStoreCreator.OpenFromOptions(options as IMetadataStoreOptions);
            if (sourceToUpdate == null)
            {
                return;
            }

            using(sourceToUpdate)
            {
                if (sourceToUpdate.IsMetadataIndexingSupported)
                {
                    sourceToUpdate.PackageIndexingProgress += Program.OnPackageIndexingProgress;
                    if (sourceToUpdate.IsReindexingRequired || options.ForceReindex)
                    {
                        Console.WriteLine("ReIndexing ...");
                        sourceToUpdate.ReIndex();
                        ConsoleOutput.WriteGreen("Done!");
                    }
                    else
                    {
                        ConsoleOutput.WriteGreen("Indexing not required!");
                    }
                }
                else
                {
                    ConsoleOutput.WriteRed("Package store does not support indexing!");
                }
            }
        }

        public static void FetchCategories(FetchCategoriesOptions options)
        {
            MicrosoftUpdate.Source.Endpoint upstreamEndpoint;
            if (!string.IsNullOrEmpty(options.UpstreamEndpoint))
            {
                upstreamEndpoint = new MicrosoftUpdate.Source.Endpoint(options.UpstreamEndpoint);
            }
            else
            {
                upstreamEndpoint = MicrosoftUpdate.Source.Endpoint.Default;
            }

            if (!string.IsNullOrEmpty(options.AccountName) &&
                !string.IsNullOrEmpty(options.AccountGuid))
            {
                throw new NotImplementedException();
            }

            var destinationStore = MetadataStoreCreator.CreateFromOptions(options as IMetadataStoreOptions);
            if (destinationStore == null)
            {
                return;
            }

            using (destinationStore)
            {
                var cancellationToken = new CancellationTokenSource();
                RefreshCategoriesAndObservedProductMap(
                    destinationStore,
                    upstreamEndpoint,
                    cancellationToken.Token);
            }

            Console.WriteLine();
            ConsoleOutput.WriteGreen("Done!");
        }

        private static void RefreshCategoriesAndObservedProductMap(
            IMetadataStore store,
            MicrosoftUpdate.Source.Endpoint upstreamEndpoint,
            CancellationToken cancellationToken)
        {
            Console.WriteLine();
            Console.WriteLine("Getting list of categories and detectoids. This might take up to 1 minute ...");

            var microsoftUpdateCategoriesSource = new UpstreamCategoriesSource(upstreamEndpoint);
            microsoftUpdateCategoriesSource.MetadataCopyProgress += Program.OnPackageCopyProgress;
            microsoftUpdateCategoriesSource.CopyTo(store, cancellationToken);

            RebuildObservedProductMap(store);
        }

        private static ObservedProductMapBuildResult RebuildObservedProductMap(IMetadataStore store)
        {
            if (!ObservedProductMapBuilder.IsSupported(store))
            {
                Console.WriteLine(
                    "The selected store does not support observed inventory; " +
                    "the detectoid-to-product map was not persisted.");
                return null;
            }

            Console.WriteLine();
            Console.WriteLine("Building the detectoid-to-product map from cached category metadata ...");
            var result = ObservedProductMapBuilder.Rebuild(store);
            Console.WriteLine(
                $"Mapped {result.MappingCount} strict detector/product pair(s) from " +
                $"{result.DetectoidCount} detectoid(s) and {result.ConcreteProductCount} concrete product(s). " +
                $"Sources: direct detectoid categories={result.DetectoidCategoryMappingCount}, " +
                $"unique direct product prerequisites={result.ProductPrerequisiteMappingCount}. " +
                "Direct ProductCategory observations, product-family expansion and transitive " +
                "prerequisite inference are disabled.");

            if (result.AmbiguousDetectoidCount > 0)
            {
                Console.WriteLine(
                    $"Ignored {result.AmbiguousDetectoidCount} ambiguous detector(s) whose direct evidence " +
                    "points to more than one concrete product.");
            }

            if (result.SkippedPackageCount > 0)
            {
                ConsoleOutput.WriteRed(
                    $"Warning: {result.SkippedPackageCount} category package(s) could not be inspected while rebuilding the map.");
            }

            return result;
        }

        public static void FetchPackagesUpdates(FetchPackagesOptions options)
        {
            var store = MetadataStoreCreator.CreateFromOptions(options as IMetadataStoreOptions);
            if (store == null)
            {
                return;
            }

            switch (options.EndpointType)
            {
                case FetchPackagesOptions.MicrosoftUpdateEndpoint:
                    FetchMicrosoftUpdatePackages(options, store);
                    break;

                default:
                    throw new NotImplementedException();
            }
        }

        private static void FetchObservedProducts(
            FetchObservedOptions options,
            IMetadataStore store)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (!string.Equals(
                options.EndpointType,
                FetchPackagesOptions.MicrosoftUpdateEndpoint,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "fetch-observed currently supports only --endpoint-type microsoft-update.");
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
                        "fetch-observed requires a local SQLite store with observed inventory support.");
                }

                if (store is not ISyncAnchorStore || store is not ISyncCheckpointStore)
                {
                    throw new InvalidOperationException(
                        "fetch-observed requires a store that supports persistent WSUS anchors and resumable checkpoints.");
                }

                var upstreamEndpoint = string.IsNullOrEmpty(options.UpstreamEndpoint)
                    ? MicrosoftUpdate.Source.Endpoint.Default
                    : new MicrosoftUpdate.Source.Endpoint(options.UpstreamEndpoint);
                var cancellationToken = new CancellationTokenSource();
                var mapWasRebuilt = false;
                var hasCachedObservedProductCatalog = HasCachedObservedProductCatalog(store);

                if (options.DryRun && options.RefreshCategories)
                {
                    throw new InvalidOperationException(
                        "--dry-run cannot be combined with --refresh-categories because a dry run never contacts Microsoft Update.");
                }

                if (options.RefreshCategories || !hasCachedObservedProductCatalog)
                {
                    if (options.DryRun)
                    {
                        throw new InvalidOperationException(
                            "The local store does not contain the product, classification and detectoid catalog required by fetch-observed. " +
                            "Run pre-fetch first, then retry --dry-run.");
                    }

                    RefreshCategoriesAndObservedProductMap(
                        store,
                        upstreamEndpoint,
                        cancellationToken.Token);
                    mapWasRebuilt = true;
                }

                var initialMapStatus = observedInventoryStore.GetDetectoidProductMapStatus();
                var mapUsesOldAlgorithm = !string.Equals(
                    initialMapStatus.AlgorithmVersion,
                    ObservedProductMapBuilder.MappingAlgorithmVersion,
                    StringComparison.Ordinal);
                var mapNeedsAutomaticRepair =
                    mapUsesOldAlgorithm
                    || (initialMapStatus.MappingCount == 0
                        && initialMapStatus.ActiveUnmappedDetectoidCount > 0);
                if (!mapWasRebuilt
                    && (options.RebuildProductMap
                        || initialMapStatus.RebuiltAt == null
                        || mapNeedsAutomaticRepair))
                {
                    if (mapNeedsAutomaticRepair && !options.RebuildProductMap)
                    {
                        Console.WriteLine(mapUsesOldAlgorithm
                            ? "The cached detectoid-to-product map was built by an older broad inference algorithm; " +
                              "rebuilding it with strict direct-only product resolution."
                            : "The cached detectoid-to-product map is empty while client detectors are present; " +
                              "rebuilding it with strict direct-only product resolution.");
                    }

                    RebuildObservedProductMap(store);
                }

                var seenSince = options.SeenWithinDays > 0
                    ? DateTimeOffset.UtcNow.AddDays(-options.SeenWithinDays)
                    : (DateTimeOffset?)null;
                var mapStatus = observedInventoryStore.GetDetectoidProductMapStatus(seenSince);
                var observedProducts = observedInventoryStore
                    .GetObservedProductCategories(seenSince)
                    .ToList();

                Console.WriteLine();
                Console.WriteLine("Observed product fetch");
                Console.WriteLine("======================");
                Console.WriteLine(seenSince.HasValue
                    ? $"Observation window : last {options.SeenWithinDays} day(s)"
                    : "Observation window : all observations");
                Console.WriteLine(
                    $"Product map       : {mapStatus.MappingCount} pair(s), " +
                    $"{mapStatus.MappedDetectoidCount} detectoid(s), " +
                    $"{mapStatus.ProductCount} product(s)");
                Console.WriteLine(
                    $"Active detectoids : {mapStatus.ActiveMappedDetectoidCount} mapped, " +
                    $"{mapStatus.ActiveUnmappedDetectoidCount} unmapped");

                if (mapStatus.ActiveUnmappedDetectoidCount > 0)
                {
                    ConsoleOutput.WriteRed(
                        "Warning: some observed detectoids do not resolve to a concrete product. " +
                        "They are ignored to avoid broad, unsafe product inference.");
                }

                if (observedProducts.Count == 0)
                {
                    Console.WriteLine("No concrete product was resolved from recent client scans. Nothing to fetch.");
                    return;
                }

                var concreteProducts = GetLatestConcreteProducts(store);
                var unknownProductIds = observedProducts
                    .Where(observed => !concreteProducts.ContainsKey(observed.ProductCategoryId))
                    .Select(observed => observed.ProductCategoryId)
                    .ToList();
                if (unknownProductIds.Count > 0)
                {
                    ConsoleOutput.WriteRed(
                        $"Warning: {unknownProductIds.Count} mapped product(s) are not present as concrete " +
                        "product categories in the current store and will be skipped. Run pre-fetch again.");
                }

                var selectedProducts = observedProducts
                    .Where(observed => concreteProducts.ContainsKey(observed.ProductCategoryId))
                    .OrderBy(observed => concreteProducts[observed.ProductCategoryId].Title ?? string.Empty)
                    .ThenBy(observed => observed.ProductCategoryId)
                    .ToList();
                if (selectedProducts.Count == 0)
                {
                    Console.WriteLine("No locally known concrete observed product remains to fetch.");
                    return;
                }

                var classificationFilter = CreateFilterListForCategory<ClassificationCategory>(
                    options.ClassificationsFilter,
                    store,
                    includeAllWhenEmpty: false);
                if (classificationFilter.Count == 0)
                {
                    throw new InvalidOperationException(
                        "At least one --classification-filter GUID is required for fetch-observed.");
                }

                var knownClassificationIds = store
                    .OfType<ClassificationCategory>()
                    .Select(classification => classification.Id.ID)
                    .ToHashSet();
                var unknownClassificationIds = classificationFilter
                    .Where(classificationId => !knownClassificationIds.Contains(classificationId))
                    .ToList();
                if (unknownClassificationIds.Count > 0)
                {
                    throw new InvalidOperationException(
                        "The following classification GUID(s) are not present in the local pre-fetch catalog: " +
                        string.Join(", ", unknownClassificationIds.Select(value => value.ToString("D"))) +
                        ". Run pre-fetch or use --refresh-categories.");
                }

                var languageFilter = CreateLanguageFilter(options.LanguageFilter);
                var stripUnrequestedLocalizedProperties =
                    languageFilter.Count > 0 && !options.KeepAllLocalizedProperties;

                Console.WriteLine(
                    $"Products selected  : {selectedProducts.Count}; " +
                    $"classifications={classificationFilter.Count}; " +
                    $"languages={(languageFilter.Count == 0 ? "all" : string.Join(",", languageFilter))}");

                if (options.DryRun)
                {
                    Console.WriteLine();
                    Console.WriteLine("Dry run: no upstream request will be sent.");
                    foreach (var observedProduct in selectedProducts)
                    {
                        var product = concreteProducts[observedProduct.ProductCategoryId];
                        var title = string.IsNullOrWhiteSpace(product.Title)
                            ? "(untitled product)"
                            : product.Title;
                        Console.WriteLine(
                            $"{observedProduct.ProductCategoryId:D}  {title}  " +
                            $"detectoids={observedProduct.DetectoidCount}  " +
                            $"last={observedProduct.LastSeen.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
                    }

                    return;
                }

                var failures = new List<Exception>();
                var productNumber = 0;
                foreach (var observedProduct in selectedProducts)
                {
                    cancellationToken.Token.ThrowIfCancellationRequested();
                    productNumber++;
                    var product = concreteProducts[observedProduct.ProductCategoryId];
                    var title = string.IsNullOrWhiteSpace(product.Title)
                        ? observedProduct.ProductCategoryId.ToString("D")
                        : product.Title;

                    Console.WriteLine();
                    Console.WriteLine(
                        $"[{productNumber}/{selectedProducts.Count}] {title} " +
                        $"({observedProduct.ProductCategoryId:D})");
                    Console.WriteLine(
                        $"Observed from {observedProduct.DetectoidCount} detectoid(s); " +
                        $"last seen {observedProduct.LastSeen.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC.");

                    var sourceFilter = new UpstreamSourceFilter(
                        new List<Guid> { observedProduct.ProductCategoryId },
                        classificationFilter,
                        languageFilter,
                        stripUnrequestedLocalizedProperties);
                    var anchorKey = BuildSyncAnchorKey(upstreamEndpoint, sourceFilter);

                    try
                    {
                        FetchMicrosoftUpdateRevisions(
                            options,
                            store,
                            upstreamEndpoint,
                            sourceFilter,
                            anchorKey,
                            cancellationToken.Token);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new InvalidOperationException(
                            $"Observed product fetch failed for {title} " +
                            $"({observedProduct.ProductCategoryId:D}).",
                            ex));
                        ConsoleOutput.WriteRed(
                            $"Product fetch failed; its stable anchor/checkpoint state was preserved: {ex.Message}");
                    }
                }

                Console.WriteLine();
                if (failures.Count > 0)
                {
                    throw new AggregateException(
                        $"fetch-observed completed with {failures.Count} failed product scope(s).",
                        failures);
                }

                ConsoleOutput.WriteGreen(
                    $"Done. {selectedProducts.Count} observed product scope(s) synchronized.");
        }

        private static Dictionary<Guid, ProductCategory> GetLatestConcreteProducts(IMetadataStore store)
        {
            var result = new Dictionary<Guid, ProductCategory>();
            foreach (var group in store.OfType<ProductCategory>().GroupBy(product => product.Id.ID))
            {
                var latest = group
                    .OrderByDescending(product => product.Id.Revision)
                    .First();
                try
                {
                    if (ObservedProductMapBuilder.IsConcreteProduct(latest))
                    {
                        result[latest.Id.ID] = latest;
                    }
                }
                catch (Exception ex)
                {
                    ConsoleOutput.WriteRed(
                        $"Warning: cannot inspect product category {latest.Id}: {ex.Message}");
                }
            }

            return result;
        }

        private static void FetchMicrosoftUpdatePackages(FetchPackagesOptions options, IMetadataStore store)
        {
            var upstreamEndpoint = string.IsNullOrEmpty(options.UpstreamEndpoint) ? MicrosoftUpdate.Source.Endpoint.Default : new MicrosoftUpdate.Source.Endpoint(options.UpstreamEndpoint);

            if (!string.IsNullOrEmpty(options.AccountName) &&
                !string.IsNullOrEmpty(options.AccountGuid))
            {
                throw new NotImplementedException();
            }

            using (store)
            {
                var cancellationToken = new CancellationTokenSource();
                if (options.RefreshCategories || StoreIsEmpty(store))
                {
                    RefreshCategoriesAndObservedProductMap(
                        store,
                        upstreamEndpoint,
                        cancellationToken.Token);
                }
                else
                {
                    Console.WriteLine("Using cached categories. Pass --refresh-categories to update them.");

                    if (store is IObservedInventoryStore observedInventoryStore)
                    {
                        var mapStatus = observedInventoryStore.GetDetectoidProductMapStatus();
                        if (mapStatus.RebuiltAt == null
                            || !string.Equals(
                                mapStatus.AlgorithmVersion,
                                ObservedProductMapBuilder.MappingAlgorithmVersion,
                                StringComparison.Ordinal))
                        {
                            RebuildObservedProductMap(store);
                        }
                    }
                }

                if (HasValues(options.Ids))
                {
                    var server = new UpstreamServerClient(upstreamEndpoint);

                    foreach (var updateId in options.Ids)
                    {
                        if (Guid.TryParse(updateId, out var updateIdGuid))
                        {
                            Console.WriteLine();
                            Console.Write($"Searching for package {updateId}");
                            var foundPackage = server.TryGetExpiredUpdate(updateIdGuid, 300, 100).GetAwaiter().GetResult();
                            if (foundPackage == null)
                            {
                                ConsoleOutput.WriteRed($" Not found!");
                            }
                            else
                            {
                                ConsoleOutput.WriteGreen($" Found!");
                                store.AddPackage(foundPackage);
                            }
                        }
                        else
                        {
                            ConsoleOutput.WriteRed($"Update id must be in GUID format: {updateId}");
                            return;
                        }
                    }
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("Creating the query ...");
                    UpstreamSourceFilter sourceFilter;
                    try
                    {
                        sourceFilter = MetadataSync.CreateValidFilterFromOptions(options, store);
                    }
                    catch (Exception ex)
                    {
                        ConsoleOutput.WriteRed(ex.Message);
                        return;
                    }

                    MetadataQuery.PrintFilter(sourceFilter, store);

                    string anchorKey = BuildSyncAnchorKey(upstreamEndpoint, sourceFilter);
                    FetchMicrosoftUpdateRevisions(
                        options,
                        store,
                        upstreamEndpoint,
                        sourceFilter,
                        anchorKey,
                        cancellationToken.Token);

                    Console.WriteLine();
                    Console.WriteLine("Done!");
                }
            }
        }

        private static void FetchMicrosoftUpdateRevisions(
            IIncrementalSyncOptions options,
            IMetadataStore store,
            MicrosoftUpdate.Source.Endpoint upstreamEndpoint,
            UpstreamSourceFilter sourceFilter,
            string anchorKey,
            CancellationToken cancellationToken)
        {
            var anchorStore = store as ISyncAnchorStore;
            var checkpointStore = store as ISyncCheckpointStore;
            var batchSize = Math.Max(1, options.MetadataBatchSize);

            if (checkpointStore != null &&
                (options.ResetSyncCheckpoint || options.IgnoreSyncAnchor) &&
                checkpointStore.TryGetSyncCheckpoint(anchorKey, out var checkpointToDiscard))
            {
                checkpointStore.ClearSyncCheckpoint(anchorKey);
                Console.WriteLine(
                    $"Discarded unfinished local checkpoint " +
                    $"({checkpointToDiscard.CompletedItems}/{checkpointToDiscard.TotalItems} revisions completed).");
            }

            string stableAnchor = null;
            if (!options.IgnoreSyncAnchor &&
                anchorStore != null &&
                anchorStore.TryGetSyncAnchor(anchorKey, out var storedAnchor))
            {
                stableAnchor = storedAnchor;
            }

            if (options.IgnoreSyncAnchor)
            {
                Console.WriteLine("Ignoring saved upstream sync anchor. A full revision list will be requested.");
            }

            if (!options.IgnoreSyncAnchor &&
                checkpointStore != null &&
                checkpointStore.TryGetSyncCheckpoint(anchorKey, out var existingCheckpoint))
            {
                if (!AnchorValuesEqual(stableAnchor, existingCheckpoint.AnchorFrom))
                {
                    throw new InvalidOperationException(
                        "The unfinished local checkpoint does not match the currently saved WSUS anchor. " +
                        "Run again with --reset-sync-checkpoint to discard the checkpoint, or add " +
                        "--ignore-sync-anchor to force a new full revision-list request.");
                }

                Console.WriteLine(
                    $"Resuming local fetch checkpoint: {existingCheckpoint.CompletedItems}/" +
                    $"{existingCheckpoint.TotalItems} revision(s) already stored. " +
                    "GetRevisionIdList will not be called again.");

                var resumeSource = CreateUpdateSource(
                    upstreamEndpoint,
                    sourceFilter,
                    existingCheckpoint.AnchorFrom,
                    batchSize);
                ResumeSyncCheckpoint(
                    store,
                    checkpointStore,
                    resumeSource,
                    anchorKey,
                    batchSize,
                    cancellationToken);
                return;
            }

            if (!string.IsNullOrEmpty(stableAnchor))
            {
                Console.WriteLine("Using saved upstream sync anchor. Only changed revisions will be requested.");
            }

            Console.WriteLine("Getting list of updates ...");
            var microsoftUpdateSource = CreateUpdateSource(
                upstreamEndpoint,
                sourceFilter,
                stableAnchor,
                batchSize);

            if (checkpointStore == null)
            {
                microsoftUpdateSource.CopyTo(store, cancellationToken);
                if (anchorStore != null && !string.IsNullOrEmpty(microsoftUpdateSource.NewAnchor))
                {
                    anchorStore.SetSyncAnchor(anchorKey, microsoftUpdateSource.NewAnchor);
                    Console.WriteLine("Saved upstream sync anchor for the next incremental fetch.");
                }

                return;
            }

            var revisionIdentities = microsoftUpdateSource.GetPackageIdentities();
            if (string.IsNullOrWhiteSpace(microsoftUpdateSource.NewAnchor))
            {
                throw new InvalidOperationException(
                    "The upstream server returned a revision list without a new anchor. " +
                    "The result cannot be checkpointed safely.");
            }

            checkpointStore.CreateSyncCheckpoint(
                anchorKey,
                stableAnchor,
                microsoftUpdateSource.NewAnchor,
                revisionIdentities);

            if (!checkpointStore.TryGetSyncCheckpoint(anchorKey, out var createdCheckpoint))
            {
                throw new InvalidOperationException(
                    "The local sync checkpoint was created but could not be read back.");
            }

            Console.WriteLine(
                $"The upstream returned {revisionIdentities.Count} revision identity/identities. " +
                $"The local checkpoint contains {createdCheckpoint.TotalItems} item(s) not already stored. " +
                "The new WSUS anchor will remain inactive until every checkpoint item is committed.");

            ResumeSyncCheckpoint(
                store,
                checkpointStore,
                microsoftUpdateSource,
                anchorKey,
                batchSize,
                cancellationToken);
        }

        private static UpstreamUpdatesSource CreateUpdateSource(
            MicrosoftUpdate.Source.Endpoint upstreamEndpoint,
            UpstreamSourceFilter sourceFilter,
            string oldAnchor,
            int batchSize)
        {
            var source = new UpstreamUpdatesSource(upstreamEndpoint, sourceFilter, oldAnchor)
            {
                BatchSize = Math.Max(1, batchSize)
            };
            source.MetadataCopyProgress += Program.OnPackageCopyProgress;
            return source;
        }

        private static void ResumeSyncCheckpoint(
            IMetadataStore store,
            ISyncCheckpointStore checkpointStore,
            UpstreamUpdatesSource microsoftUpdateSource,
            string anchorKey,
            int batchSize,
            CancellationToken cancellationToken)
        {
            // Repair the safe crash window where package rows committed but the
            // corresponding checkpoint status update did not run yet.
            checkpointStore.ReconcileSyncCheckpoint(anchorKey);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!checkpointStore.TryGetSyncCheckpoint(anchorKey, out var checkpoint))
                {
                    throw new InvalidOperationException("The local synchronization checkpoint disappeared before completion.");
                }

                if (checkpoint.PendingItems == 0)
                {
                    // This transaction both promotes AnchorTo and deletes the
                    // checkpoint. There is no state where the anchor is advanced
                    // while pending items still exist.
                    checkpointStore.CompleteSyncCheckpoint(anchorKey);
                    Console.WriteLine();
                    Console.WriteLine("Checkpoint complete. Promoted the new WSUS anchor atomically.");
                    return;
                }

                var pendingItems = checkpointStore
                    .GetPendingSyncCheckpointItems(anchorKey, batchSize)
                    .ToList();
                if (pendingItems.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"The checkpoint reports {checkpoint.PendingItems} pending item(s), but no pending identities could be read.");
                }

                var pendingUpdates = pendingItems
                    .Select(identity => identity as MicrosoftUpdatePackageIdentity
                        ?? throw new InvalidOperationException($"Unsupported checkpoint identity: {identity}"))
                    .ToList();

                checkpointStore.MarkSyncCheckpointItemsAttempted(anchorKey, pendingItems);
                try
                {
                    microsoftUpdateSource.CopyPackagesTo(
                        store,
                        pendingUpdates,
                        cancellationToken,
                        completedItems => checkpointStore.MarkSyncCheckpointItemsCompleted(
                            anchorKey,
                            completedItems));
                }
                catch (Exception ex)
                {
                    try
                    {
                        checkpointStore.MarkSyncCheckpointItemsFailed(anchorKey, pendingItems, ex.Message);
                        checkpointStore.ReconcileSyncCheckpoint(anchorKey);
                    }
                    catch
                    {
                        // Preserve the original network/storage error. The next
                        // run will reconcile package rows against pending items.
                    }

                    if (checkpointStore.TryGetSyncCheckpoint(anchorKey, out var failedCheckpoint))
                    {
                        Console.WriteLine();
                        ConsoleOutput.WriteRed(
                            $"Fetch interrupted. Checkpoint kept: {failedCheckpoint.CompletedItems}/" +
                            $"{failedCheckpoint.TotalItems} revision(s) complete. Rerun the same command to resume.");
                    }

                    throw new InvalidOperationException(
                        "The fetch checkpoint was retained and the stable WSUS anchor was not advanced.",
                        ex);
                }

                if (checkpointStore.TryGetSyncCheckpoint(anchorKey, out var updatedCheckpoint))
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        $"Checkpoint progress: {updatedCheckpoint.CompletedItems}/" +
                        $"{updatedCheckpoint.TotalItems} revision(s) stored; " +
                        $"{updatedCheckpoint.PendingItems} remaining.");
                }
            }
        }

        private static bool AnchorValuesEqual(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
        }

        private static void UpdateConsoleForMessageRefresh()
        {
            if (!Console.IsOutputRedirected)
            {
                Console.CursorLeft = 0;
            }
            else
            {
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Handles progress notifications from a metadata query on an upstream server.
        /// Prints progress information to the console
        /// </summary>
        /// <param name="sender">The upstream server client that raised the event</param>
        /// <param name="e">Progress information</param>
        private static void Server_MetadataQueryProgress(object sender, MetadataQueryProgress e)
        {
            switch (e.CurrentTask)
            {
                case MetadataQueryStage.AuthenticateStart:
                    Console.Write("Acquiring new access token...");
                    break;

                case MetadataQueryStage.GetServerConfigStart:
                    Console.Write("Retrieving service configuration data...");
                    break;

                case MetadataQueryStage.AuthenticateEnd:
                case MetadataQueryStage.GetServerConfigEnd:
                case MetadataQueryStage.GetRevisionIdsEnd:
                    ConsoleOutput.WriteGreen("Done!");
                    break;

                case MetadataQueryStage.GetRevisionIdsStart:
                    Console.Write("Retrieving revision IDs...");
                    break;

                case MetadataQueryStage.GetUpdateMetadataStart:
                    Console.Write("Retrieving updates metadata [{0}]: 0%", e.Maximum);
                    break;

                case MetadataQueryStage.GetUpdateMetadataEnd:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Retrieving updates metadata [{0}]: 100.00%", e.Maximum);
                    ConsoleOutput.WriteGreen(" Done!");
                    break;

                case MetadataQueryStage.GetUpdateMetadataProgress:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Retrieving updates metadata [{0}]: {1:000.00}%", e.Maximum, e.PercentDone);
                    break;
            }
        }

        private static bool HasValues(IEnumerable<string> values)
        {
            return values != null && values.Any(value => !string.IsNullOrWhiteSpace(value));
        }

        private static bool StoreIsEmpty(IMetadataStore metadataSource)
        {
            return metadataSource.GetPackageIdentities().Count == 0;
        }

        private static bool HasCachedObservedProductCatalog(IMetadataStore metadataSource)
        {
            return metadataSource.OfType<ProductCategory>().Any()
                && metadataSource.OfType<ClassificationCategory>().Any()
                && metadataSource.OfType<DetectoidCategory>().Any();
        }

        private static List<Guid> CreateFilterListForCategory<T>(IEnumerable<string> userFilterList, IMetadataStore metadataSource, bool includeAllWhenEmpty)
        {
            var userFilters = userFilterList?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList() ?? new List<string>();

            List<Guid> filterList;
            if (userFilters.Count > 0)
            {
                filterList = new List<Guid>();
                foreach (var guidString in userFilters)
                {
                    if (!Guid.TryParse(guidString, out Guid guid))
                    {
                        throw new Exception($"Invalid GUID in filter: {guidString}");
                    }

                    filterList.Add(guid);
                }
            }
            else if (includeAllWhenEmpty)
            {
                filterList = metadataSource.OfType<T>()
                    .Select(update => (update as MicrosoftUpdatePackage).Id.ID)
                    .ToList();

                if (filterList.Count == 0)
                {
                    throw new Exception("No category information available to create a filter. Run pre-fetch first or use --refresh-categories.");
                }
            }
            else
            {
                filterList = new List<Guid>();
            }

            return filterList.Distinct().ToList();
        }

        private static List<int> CreateLanguageFilter(IEnumerable<string> requestedLanguages)
        {
            var languageOptions = requestedLanguages?.ToList() ?? new List<string>();
            if (languageOptions.Count == 0)
            {
                // Default to English only. This avoids syncing every localized metadata branch.
                return new List<int> { 1033 };
            }

            if (languageOptions.Any(language =>
                string.Equals(language, "all", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language, "*", StringComparison.OrdinalIgnoreCase)))
            {
                return new List<int>();
            }

            var languages = new List<int>();
            foreach (var language in languageOptions)
            {
                if (int.TryParse(language, out var lcid))
                {
                    languages.Add(lcid);
                    continue;
                }

                var normalizedLanguage = language.Trim().ToLowerInvariant();
                switch (normalizedLanguage)
                {
                    case "en":
                    case "en-us":
                    case "english":
                        languages.Add(1033);
                        break;
                    case "fr":
                    case "fr-fr":
                    case "french":
                        languages.Add(1036);
                        break;
                    case "de":
                    case "de-de":
                    case "german":
                        languages.Add(1031);
                        break;
                    case "es":
                    case "es-es":
                    case "spanish":
                        languages.Add(3082);
                        break;
                    case "it":
                    case "it-it":
                    case "italian":
                        languages.Add(1040);
                        break;
                    default:
                        throw new Exception($"Unsupported language filter '{language}'. Use an LCID such as 1033, a known short name such as en, or all.");
                }
            }

            return languages.Distinct().ToList();
        }

        private static string BuildSyncAnchorKey(MicrosoftUpdate.Source.Endpoint endpoint, UpstreamSourceFilter filter)
        {
            var keyData = new
            {
                Type = "microsoft-update-revision-list",
                Endpoint = endpoint.URI,
                Products = filter.ProductsFilter.OrderBy(value => value).Select(value => value.ToString("D")).ToList(),
                Classifications = filter.ClassificationsFilter.OrderBy(value => value).Select(value => value.ToString("D")).ToList(),
                Languages = filter.LanguagesFilter.OrderBy(value => value).ToList()
            };

            var json = JsonConvert.SerializeObject(keyData);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
            return "sync-anchor:mu:" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static UpstreamSourceFilter CreateValidFilterFromOptions(FetchPackagesOptions options, IMetadataStore metadataSource)
        {
            var hasProductFilter = HasValues(options.ProductsFilter);
            if (!hasProductFilter && !options.AllowAllProducts)
            {
                throw new Exception(
                    "No --product-filter was provided. A classification-only fetch asks Microsoft Update for that classification across every product and can take hours or create a huge store. " +
                    "Add --product-filter <PRODUCT_GUID>, or add --allow-all-products if you really want the full catalog.");
            }

            if (!hasProductFilter && options.AllowAllProducts)
            {
                ConsoleOutput.WriteRed("Warning: --allow-all-products selected. This can be extremely slow and large.");
            }

            List<Guid> productFilter = CreateFilterListForCategory<ProductCategory>(
                options.ProductsFilter,
                metadataSource,
                includeAllWhenEmpty: true);

            List<Guid> classificationFilter = CreateFilterListForCategory<ClassificationCategory>(
                options.ClassificationsFilter,
                metadataSource,
                includeAllWhenEmpty: true);

            List<int> languageFilter = CreateLanguageFilter(options.LanguageFilter);
            bool stripUnrequestedLocalizedProperties = languageFilter.Count > 0 && !options.KeepAllLocalizedProperties;

            return new UpstreamSourceFilter(productFilter, classificationFilter, languageFilter, stripUnrequestedLocalizedProperties);
        }
    }
}
