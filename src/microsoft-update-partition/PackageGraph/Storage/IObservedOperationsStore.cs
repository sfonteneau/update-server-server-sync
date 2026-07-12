// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>State of one scheduled fetch-observed execution.</summary>
    public enum ObservedFetchRunStatus
    {
        /// <summary>The process is still executing or ended before finalization.</summary>
        Running = 0,

        /// <summary>Every selected synchronization phase completed successfully.</summary>
        Succeeded = 1,

        /// <summary>Some durable work completed, but at least one phase failed.</summary>
        Partial = 2,

        /// <summary>No selected phase completed successfully.</summary>
        Failed = 3,

        /// <summary>The command only inspected the planned work.</summary>
        DryRun = 4
    }

    /// <summary>Persistent execution record for fetch-observed.</summary>
    public sealed class ObservedFetchRunInfo
    {
        /// <summary>Gets the monotonically increasing local run identifier.</summary>
        public long RunId { get; }

        /// <summary>Gets when the execution started.</summary>
        public DateTimeOffset StartedAt { get; }

        /// <summary>Gets when the execution completed, if it was finalized.</summary>
        public DateTimeOffset? CompletedAt { get; }

        /// <summary>Gets the final or current execution status.</summary>
        public ObservedFetchRunStatus Status { get; }

        /// <summary>Gets the observation-age window selected by the command.</summary>
        public int SeenWithinDays { get; }

        /// <summary>Gets whether observed products were selected.</summary>
        public bool IncludeProducts { get; }

        /// <summary>Gets whether observed drivers were selected.</summary>
        public bool IncludeDrivers { get; }

        /// <summary>Gets whether compatible PnP identifiers were selected.</summary>
        public bool IncludeCompatibleIds { get; }

        /// <summary>Gets whether the execution was a dry run.</summary>
        public bool DryRun { get; }

        /// <summary>Gets the package count before synchronization.</summary>
        public long PackageCountBefore { get; }

        /// <summary>Gets the package count after synchronization, when known.</summary>
        public long? PackageCountAfter { get; }

        /// <summary>Gets a compact execution summary.</summary>
        public string Summary { get; }

        /// <summary>Gets the persisted error summary, if any.</summary>
        public string Error { get; }

        /// <summary>Creates one persisted fetch-observed execution record.</summary>
        public ObservedFetchRunInfo(
            long runId,
            DateTimeOffset startedAt,
            DateTimeOffset? completedAt,
            ObservedFetchRunStatus status,
            int seenWithinDays,
            bool includeProducts,
            bool includeDrivers,
            bool includeCompatibleIds,
            bool dryRun,
            long packageCountBefore,
            long? packageCountAfter,
            string summary,
            string error)
        {
            RunId = runId;
            StartedAt = startedAt.ToUniversalTime();
            CompletedAt = completedAt?.ToUniversalTime();
            Status = status;
            SeenWithinDays = Math.Max(0, seenWithinDays);
            IncludeProducts = includeProducts;
            IncludeDrivers = includeDrivers;
            IncludeCompatibleIds = includeCompatibleIds;
            DryRun = dryRun;
            PackageCountBefore = Math.Max(0, packageCountBefore);
            PackageCountAfter = packageCountAfter.HasValue
                ? Math.Max(0, packageCountAfter.Value)
                : null;
            Summary = summary;
            Error = error;
        }
    }

    /// <summary>Aggregated synchronization state used by observed-status.</summary>
    public sealed class ObservedSyncOperationalStatus
    {
        /// <summary>Gets the number of stable software synchronization anchors.</summary>
        public long StableProductAnchorCount { get; }

        /// <summary>Gets the number of stable per-identifier driver anchors.</summary>
        public long StableDriverIdentifierAnchorCount { get; }

        /// <summary>Gets the number of incomplete metadata checkpoints.</summary>
        public long PendingCheckpointCount { get; }

        /// <summary>Gets the number of checkpoint items still pending.</summary>
        public long PendingCheckpointItemCount { get; }

        /// <summary>Gets the number of checkpoint items already durable.</summary>
        public long CompletedCheckpointItemCount { get; }

        /// <summary>Gets when the oldest incomplete checkpoint was created.</summary>
        public DateTimeOffset? OldestCheckpoint { get; }

        /// <summary>Gets when an incomplete checkpoint was most recently updated.</summary>
        public DateTimeOffset? NewestCheckpointUpdate { get; }

        /// <summary>Creates an aggregate operational status snapshot.</summary>
        public ObservedSyncOperationalStatus(
            long stableProductAnchorCount,
            long stableDriverIdentifierAnchorCount,
            long pendingCheckpointCount,
            long pendingCheckpointItemCount,
            long completedCheckpointItemCount,
            DateTimeOffset? oldestCheckpoint,
            DateTimeOffset? newestCheckpointUpdate)
        {
            StableProductAnchorCount = Math.Max(0, stableProductAnchorCount);
            StableDriverIdentifierAnchorCount = Math.Max(0, stableDriverIdentifierAnchorCount);
            PendingCheckpointCount = Math.Max(0, pendingCheckpointCount);
            PendingCheckpointItemCount = Math.Max(0, pendingCheckpointItemCount);
            CompletedCheckpointItemCount = Math.Max(0, completedCheckpointItemCount);
            OldestCheckpoint = oldestCheckpoint?.ToUniversalTime();
            NewestCheckpointUpdate = newestCheckpointUpdate?.ToUniversalTime();
        }
    }

    /// <summary>
    /// Optional operational capabilities for the observed-inventory workflow.
    /// </summary>
    public interface IObservedOperationsStore
    {
        /// <summary>Starts and persists one fetch-observed execution.</summary>
        long StartObservedFetchRun(
            DateTimeOffset startedAt,
            int seenWithinDays,
            bool includeProducts,
            bool includeDrivers,
            bool includeCompatibleIds,
            bool dryRun,
            long packageCountBefore);

        /// <summary>Completes a persisted fetch-observed execution.</summary>
        void CompleteObservedFetchRun(
            long runId,
            DateTimeOffset completedAt,
            ObservedFetchRunStatus status,
            long packageCountAfter,
            string summary,
            string error);

        /// <summary>Gets the most recent fetch-observed executions.</summary>
        IReadOnlyList<ObservedFetchRunInfo> GetObservedFetchRuns(int maxCount);

        /// <summary>Gets aggregate anchor and checkpoint state.</summary>
        ObservedSyncOperationalStatus GetObservedSyncOperationalStatus();

        /// <summary>
        /// Counts driver identifier anchors that are both older than the cutoff
        /// and no longer represented by an active observation.
        /// </summary>
        long CountInactiveDriverSyncState(DateTimeOffset olderThan);

        /// <summary>
        /// Deletes driver identifier anchors that are both older than the cutoff
        /// and no longer represented by an active observation.
        /// </summary>
        long PruneInactiveDriverSyncState(DateTimeOffset olderThan);
    }
}
