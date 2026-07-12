// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.Storage;
using System;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    internal static class ObservedInventoryMaintenanceCommand
    {
        public static void Prune(PruneObservedInventoryOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.OlderThanDays <= 0)
            {
                ConsoleOutput.WriteRed("--older-than-days must be greater than zero.");
                return;
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

            var operationsStore = store as IObservedOperationsStore;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-options.OlderThanDays);
            var status = observedInventoryStore.GetObservedInventoryStatus(cutoff);
            var staleDetectoids = Math.Max(0, status.Detectoids.TotalCount - status.Detectoids.SelectedCount);
            var stalePnpIds = Math.Max(0, status.PnpHardwareIds.TotalCount - status.PnpHardwareIds.SelectedCount);
            var staleCompatibleIds = Math.Max(0, status.CompatibleIds.TotalCount - status.CompatibleIds.SelectedCount);
            var staleComputerIds = Math.Max(0, status.ComputerIds.TotalCount - status.ComputerIds.SelectedCount);
            var staleObservations = staleDetectoids + stalePnpIds + staleCompatibleIds + staleComputerIds;
            var staleDriverAnchors = options.PruneDriverAnchors && operationsStore != null
                ? operationsStore.CountInactiveDriverSyncState(cutoff)
                : 0;

            Console.WriteLine("Observed inventory maintenance");
            Console.WriteLine("==============================");
            Console.WriteLine($"Cutoff                    : {cutoff:yyyy-MM-dd HH:mm:ss} UTC");
            Console.WriteLine($"Stale detectoids          : {staleDetectoids}");
            Console.WriteLine($"Stale PnP hardware IDs    : {stalePnpIds}");
            Console.WriteLine($"Stale compatible IDs      : {staleCompatibleIds}");
            Console.WriteLine($"Stale computer IDs        : {staleComputerIds}");
            Console.WriteLine($"Stale observations total  : {staleObservations}");
            Console.WriteLine(options.PruneDriverAnchors
                ? $"Stale driver anchors       : {staleDriverAnchors}"
                : "Stale driver anchors       : not requested");

            if (options.DryRun)
            {
                Console.WriteLine();
                Console.WriteLine("Dry run: SQLite was not modified.");
                return;
            }

            var deletedObservations = observedInventoryStore.PruneObservedInventory(cutoff);
            long deletedDriverAnchors = 0;
            if (options.PruneDriverAnchors)
            {
                if (operationsStore == null)
                {
                    ConsoleOutput.WriteRed(
                        "The store cannot prune per-identifier driver anchors; observations were still pruned.");
                }
                else
                {
                    deletedDriverAnchors = operationsStore.PruneInactiveDriverSyncState(cutoff);
                }
            }

            Console.WriteLine();
            ConsoleOutput.WriteGreen(
                $"Deleted {deletedObservations} stale observation(s) and " +
                $"{deletedDriverAnchors} stale driver anchor(s).");
        }
    }
}
