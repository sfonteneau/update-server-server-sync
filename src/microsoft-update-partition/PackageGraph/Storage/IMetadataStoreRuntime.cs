// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Identifies one published version of the metadata catalog. The generation
    /// changes only after package rows and their indexes have been flushed.
    /// </summary>
    public sealed class MetadataStoreGenerationInfo
    {
        /// <summary>Gets the monotonically increasing catalog generation.</summary>
        public long Generation { get; }

        /// <summary>Gets when this catalog generation was published.</summary>
        public DateTimeOffset LastChanged { get; }

        /// <summary>Gets whether publication is currently deferred by a resumable fetch.</summary>
        public bool PublicationDeferred { get; }

        /// <summary>Gets whether durable package/index changes await publication.</summary>
        public bool HasUnpublishedChanges { get; }

        /// <summary>Creates catalog generation information.</summary>
        public MetadataStoreGenerationInfo(
            long generation,
            DateTimeOffset lastChanged,
            bool publicationDeferred = false,
            bool hasUnpublishedChanges = false)
        {
            Generation = Math.Max(0, generation);
            LastChanged = lastChanged == default
                ? DateTimeOffset.MinValue
                : lastChanged.ToUniversalTime();
            PublicationDeferred = publicationDeferred;
            HasUnpublishedChanges = hasUnpublishedChanges;
        }
    }

    /// <summary>
    /// Optional capability implemented by a long-lived metadata store that can
    /// detect changes published by another process and reload its in-memory maps
    /// and indexes without restarting the update server.
    /// </summary>
    public interface IReloadableMetadataStore
    {
        /// <summary>Gets the catalog generation currently loaded in memory.</summary>
        MetadataStoreGenerationInfo GetLoadedMetadataGeneration();

        /// <summary>Gets the latest catalog generation persisted in the store.</summary>
        MetadataStoreGenerationInfo GetPersistentMetadataGeneration();

        /// <summary>
        /// Reloads package maps and indexes when the persisted generation is newer.
        /// Returns true when a reload occurred.
        /// </summary>
        bool ReloadMetadataIfChanged();
    }

    /// <summary>
    /// Optional capability used by a resumable synchronization command to make
    /// package rows and indexes durable without exposing a partial catalog to a
    /// long-lived update server. The deferred state is persisted, so a process
    /// termination cannot accidentally publish an incomplete checkpoint.
    /// </summary>
    public interface IMetadataCatalogPublicationControl
    {
        /// <summary>
        /// Defers catalog generation publication. Flush still persists package
        /// rows and indexes required for checkpoint recovery.
        /// </summary>
        void DeferCatalogPublication();

        /// <summary>
        /// Flushes pending index changes, publishes one catalog generation when
        /// metadata changed, and clears the persistent deferred state.
        /// </summary>
        MetadataStoreGenerationInfo PublishDeferredCatalogChanges();
    }
}
