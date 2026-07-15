// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.Storage;
using System;
using System.Linq;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    internal static class ObservedInventoryStatusCommand
    {
        public static void Show(ObservedInventoryStatusOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            using var store = MetadataStoreCreator.OpenFromOptions(options);
            if (store == null)
            {
                return;
            }

            if (store is not IObservedInventoryStore observedInventoryStore)
            {
                ConsoleOutput.WriteRed(
                    "This metadata store does not support observed client inventory. " +
                    "Use a local SQLite store.");
                return;
            }

            var seenSince = options.SeenWithinDays > 0
                ? DateTimeOffset.UtcNow.AddDays(-options.SeenWithinDays)
                : (DateTimeOffset?)null;
            var limit = Math.Max(0, Math.Min(options.Limit, 1000));
            var status = observedInventoryStore.GetObservedInventoryStatus(seenSince);

            Console.WriteLine("Observed client inventory");
            Console.WriteLine("=========================");
            Console.WriteLine(seenSince.HasValue
                ? $"Active window           : last {options.SeenWithinDays} day(s)"
                : "Active window           : all observations");

            if (store is IReloadableMetadataStore reloadableStore)
            {
                var generation = reloadableStore.GetPersistentMetadataGeneration();
                Console.WriteLine(
                    $"Catalog generation      : {generation.Generation,8}  " +
                    $"published={FormatTimestamp(generation.LastChanged)}");
                Console.WriteLine(
                    $"Catalog publication     : " +
                    (generation.PublicationDeferred
                        ? generation.HasUnpublishedChanges
                            ? "deferred; durable changes are waiting for a successful fetch-observed"
                            : "deferred; no unpublished package changes"
                        : "published"));
            }

            WriteSummary("Detectoids", status.Detectoids);
            WriteSummary("PnP hardware IDs", status.PnpHardwareIds);
            WriteSummary("Compatible IDs", status.CompatibleIds);
            WriteSummary("Computer hardware IDs", status.ComputerIds);

            var productMapStatus = observedInventoryStore.GetDetectoidProductMapStatus(seenSince);
            Console.WriteLine(
                $"Product map             : {productMapStatus.MappingCount,8} pair(s), " +
                $"{productMapStatus.MappedDetectoidCount} detectoid(s), " +
                $"{productMapStatus.ProductCount} product(s), " +
                $"rebuilt={FormatTimestamp(productMapStatus.RebuiltAt)}");
            Console.WriteLine(
                $"Detectoid resolution    : {productMapStatus.ActiveMappedDetectoidCount,8} mapped / " +
                $"{productMapStatus.ActiveUnmappedDetectoidCount,8} unmapped");

            if (store is IObservedOperationsStore operationsStore)
            {
                WriteOperationalStatus(operationsStore.GetObservedSyncOperationalStatus());
                WriteFetchHistory(
                    operationsStore.GetObservedFetchRuns(
                        Math.Max(0, Math.Min(options.HistoryLimit, 100))));
            }

            if (limit == 0)
            {
                return;
            }

            WriteObservedProducts(
                store,
                observedInventoryStore.GetObservedProductCategories(seenSince).Take(limit));
            WriteDetectoids(
                observedInventoryStore.GetObservedDetectoids(seenSince).Take(limit));
            WriteIdentifiers(
                "Recent PnP hardware IDs",
                observedInventoryStore.GetObservedPnpHardwareIds(seenSince).Take(limit));
            WriteIdentifiers(
                "Recent compatible IDs",
                observedInventoryStore.GetObservedCompatibleIds(seenSince).Take(limit));
            WriteComputerIds(
                observedInventoryStore.GetObservedComputerIds(seenSince).Take(limit));
        }

        private static void WriteSummary(string label, ObservedInventoryKindStatus status)
        {
            var staleCount = Math.Max(0, status.TotalCount - status.SelectedCount);
            Console.WriteLine(
                $"{label,-24}: {status.SelectedCount,8} active / {staleCount,8} stale / {status.TotalCount,8} total  " +
                $"first={FormatTimestamp(status.FirstSeen)}  " +
                $"last={FormatTimestamp(status.LastSeen)}");
        }

        private static void WriteOperationalStatus(ObservedSyncOperationalStatus status)
        {
            Console.WriteLine(
                $"Software anchor scopes  : {status.StableProductAnchorCount,8}");
            Console.WriteLine(
                $"Driver identifier state : {status.StableDriverIdentifierAnchorCount,8} anchor(s)");
            Console.WriteLine(
                $"Pending checkpoints     : {status.PendingCheckpointCount,8} checkpoint(s), " +
                $"{status.PendingCheckpointItemCount} item(s) pending, " +
                $"{status.CompletedCheckpointItemCount} item(s) durable");

            if (status.PendingCheckpointCount > 0)
            {
                Console.WriteLine(
                    $"Checkpoint timestamps  : oldest={FormatTimestamp(status.OldestCheckpoint)}  " +
                    $"latest={FormatTimestamp(status.NewestCheckpointUpdate)}");
            }
        }

        private static void WriteFetchHistory(
            System.Collections.Generic.IReadOnlyList<ObservedFetchRunInfo> runs)
        {
            if (runs == null || runs.Count == 0)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Recent fetch-observed executions");
            Console.WriteLine("--------------------------------");
            foreach (var run in runs)
            {
                var duration = run.CompletedAt.HasValue
                    ? run.CompletedAt.Value - run.StartedAt
                    : (TimeSpan?)null;
                var packageDelta = run.PackageCountAfter.HasValue
                    ? run.PackageCountAfter.Value - run.PackageCountBefore
                    : (long?)null;
                Console.WriteLine(
                    $"#{run.RunId} {run.Status,-9} " +
                    $"start={FormatTimestamp(run.StartedAt)} " +
                    $"duration={(duration.HasValue ? duration.Value.ToString(@"hh\:mm\:ss") : "-")} " +
                    $"packages={(packageDelta.HasValue ? packageDelta.Value.ToString("+0;-0;0") : "-")} " +
                    $"window={(run.SeenWithinDays == 0 ? "all" : run.SeenWithinDays + "d")} " +
                    $"products={(run.IncludeProducts ? "yes" : "no")} " +
                    $"drivers={(run.IncludeDrivers ? "yes" : "no")} " +
                    $"compatible={(run.IncludeCompatibleIds ? "yes" : "no")}" +
                    (run.DryRun ? " dry-run" : string.Empty));

                if (!string.IsNullOrWhiteSpace(run.Summary))
                {
                    Console.WriteLine($"    {run.Summary}");
                }

                if (!string.IsNullOrWhiteSpace(run.Error))
                {
                    Console.WriteLine($"    error: {run.Error.Replace(Environment.NewLine, " | ")}");
                }
            }
        }

        private static void WriteObservedProducts(
            IMetadataStore store,
            System.Collections.Generic.IEnumerable<ObservedProductCategory> products)
        {
            Console.WriteLine();
            Console.WriteLine("Resolved observed products");
            Console.WriteLine("--------------------------");

            var values = products.ToList();
            if (values.Count == 0)
            {
                Console.WriteLine("(none)");
                return;
            }

            var titles = store
                .OfType<ProductCategory>()
                .GroupBy(product => product.Id.ID)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(product => product.Id.Revision)
                        .Select(product => product.Title)
                        .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)));

            foreach (var product in values)
            {
                titles.TryGetValue(product.ProductCategoryId, out var title);
                Console.WriteLine(
                    $"{product.ProductCategoryId:D}  " +
                    $"title={title ?? "(unknown)"}  " +
                    $"detectoids={product.DetectoidCount}  " +
                    $"last={FormatTimestamp(product.LastSeen)}");
            }
        }

        private static void WriteDetectoids(
            System.Collections.Generic.IEnumerable<ObservedDetectoidObservation> detectoids)
        {
            Console.WriteLine();
            Console.WriteLine("Recent detectoids");
            Console.WriteLine("-----------------");

            var values = detectoids.ToList();
            if (values.Count == 0)
            {
                Console.WriteLine("(none)");
                return;
            }

            foreach (var detectoid in values)
            {
                Console.WriteLine(
                    $"{detectoid.UpdateId:D} rev={detectoid.RevisionNumber} " +
                    $"last={FormatTimestamp(detectoid.LastSeen)} observations={detectoid.ObservationCount}");
            }
        }

        private static void WriteIdentifiers(
            string title,
            System.Collections.Generic.IEnumerable<ObservedIdentifierObservation> identifiers)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine(new string('-', title.Length));

            var values = identifiers.ToList();
            if (values.Count == 0)
            {
                Console.WriteLine("(none)");
                return;
            }

            foreach (var identifier in values)
            {
                Console.WriteLine(
                    $"{identifier.Identifier}  " +
                    $"last={FormatTimestamp(identifier.LastSeen)} observations={identifier.ObservationCount}");
            }
        }

        private static void WriteComputerIds(
            System.Collections.Generic.IEnumerable<ObservedComputerIdObservation> identifiers)
        {
            Console.WriteLine();
            Console.WriteLine("Recent computer hardware IDs");
            Console.WriteLine("----------------------------");

            var values = identifiers.ToList();
            if (values.Count == 0)
            {
                Console.WriteLine("(none)");
                return;
            }

            foreach (var identifier in values)
            {
                Console.WriteLine(
                    $"{identifier.ComputerId:D}  " +
                    $"last={FormatTimestamp(identifier.LastSeen)} observations={identifier.ObservationCount}");
            }
        }

        private static string FormatTimestamp(DateTimeOffset? timestamp)
        {
            return timestamp.HasValue && timestamp.Value != DateTimeOffset.MinValue
                ? timestamp.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
                : "-";
        }
    }
}
