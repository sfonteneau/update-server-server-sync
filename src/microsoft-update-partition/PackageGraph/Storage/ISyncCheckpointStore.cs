// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Describes an unfinished upstream metadata synchronization.
    /// The upstream anchor in <see cref="AnchorTo"/> must not become the active
    /// anchor until every checkpoint item has been stored successfully.
    /// </summary>
    public sealed class SyncCheckpointInfo
    {
        public string AnchorKey { get; }

        public string AnchorFrom { get; }

        public string AnchorTo { get; }

        public int TotalItems { get; }

        public int CompletedItems { get; }

        public int PendingItems => Math.Max(0, TotalItems - CompletedItems);

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset UpdatedAt { get; }

        public SyncCheckpointInfo(
            string anchorKey,
            string anchorFrom,
            string anchorTo,
            int totalItems,
            int completedItems,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt)
        {
            AnchorKey = anchorKey;
            AnchorFrom = anchorFrom;
            AnchorTo = anchorTo;
            TotalItems = totalItems;
            CompletedItems = completedItems;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
        }
    }

    /// <summary>
    /// Optional capability implemented by metadata stores that can checkpoint a
    /// WSUS GetRevisionIdList result locally. Package rows may be committed batch
    /// by batch, while the new WSUS anchor is promoted atomically only after all
    /// revision identities in the checkpoint are complete.
    /// </summary>
    public interface ISyncCheckpointStore
    {
        bool TryGetSyncCheckpoint(string anchorKey, out SyncCheckpointInfo checkpoint);

        void CreateSyncCheckpoint(
            string anchorKey,
            string anchorFrom,
            string anchorTo,
            IReadOnlyList<IPackageIdentity> packageIdentities);

        IReadOnlyList<IPackageIdentity> GetPendingSyncCheckpointItems(string anchorKey, int maxCount);

        void MarkSyncCheckpointItemsAttempted(string anchorKey, IEnumerable<IPackageIdentity> packageIdentities);

        void MarkSyncCheckpointItemsCompleted(string anchorKey, IEnumerable<IPackageIdentity> packageIdentities);

        void MarkSyncCheckpointItemsFailed(string anchorKey, IEnumerable<IPackageIdentity> packageIdentities, string error);

        /// <summary>
        /// Marks checkpoint items complete when their package row is already in
        /// the store. This repairs the safe crash window between package commit
        /// and checkpoint-item commit.
        /// </summary>
        void ReconcileSyncCheckpoint(string anchorKey);

        /// <summary>
        /// Atomically promotes the checkpoint's AnchorTo value to the active
        /// anchor and removes the completed checkpoint. Throws if items remain.
        /// </summary>
        void CompleteSyncCheckpoint(string anchorKey);

        void ClearSyncCheckpoint(string anchorKey);
    }
}
