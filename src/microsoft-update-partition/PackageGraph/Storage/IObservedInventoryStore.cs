// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Identifies a detectoid revision reported as installed/applicable by a
    /// Windows Update client.
    /// </summary>
    public sealed class ObservedDetectoidIdentity
    {
        /// <summary>Gets the global Microsoft Update identifier.</summary>
        public Guid UpdateId { get; }

        /// <summary>Gets the observed revision number.</summary>
        public int RevisionNumber { get; }

        /// <summary>Creates a detectoid identity.</summary>
        public ObservedDetectoidIdentity(Guid updateId, int revisionNumber)
        {
            UpdateId = updateId;
            RevisionNumber = revisionNumber;
        }
    }

    /// <summary>
    /// A set of observations collected from one SyncUpdates request. No client,
    /// machine or device provenance is retained.
    /// </summary>
    public sealed class ObservedInventoryBatch
    {
        /// <summary>Gets when the request was observed.</summary>
        public DateTimeOffset ObservedAt { get; }

        /// <summary>Gets detectoids reported as installed/applicable.</summary>
        public IReadOnlyCollection<ObservedDetectoidIdentity> Detectoids { get; }

        /// <summary>Gets PnP hardware identifiers reported by the client.</summary>
        public IReadOnlyCollection<string> PnpHardwareIds { get; }

        /// <summary>Gets compatible identifiers reported by the client.</summary>
        public IReadOnlyCollection<string> CompatibleIds { get; }

        /// <summary>Gets computer hardware identifiers reported by the client.</summary>
        public IReadOnlyCollection<Guid> ComputerIds { get; }

        /// <summary>Gets whether the batch contains no usable observations.</summary>
        public bool IsEmpty =>
            Detectoids.Count == 0 &&
            PnpHardwareIds.Count == 0 &&
            CompatibleIds.Count == 0 &&
            ComputerIds.Count == 0;

        /// <summary>Creates an observed inventory batch.</summary>
        public ObservedInventoryBatch(
            DateTimeOffset observedAt,
            IEnumerable<ObservedDetectoidIdentity> detectoids,
            IEnumerable<string> pnpHardwareIds,
            IEnumerable<string> compatibleIds,
            IEnumerable<Guid> computerIds)
        {
            ObservedAt = observedAt == default
                ? DateTimeOffset.UtcNow
                : observedAt.ToUniversalTime();
            Detectoids = (detectoids ?? Array.Empty<ObservedDetectoidIdentity>()).ToArray();
            PnpHardwareIds = (pnpHardwareIds ?? Array.Empty<string>()).ToArray();
            CompatibleIds = (compatibleIds ?? Array.Empty<string>()).ToArray();
            ComputerIds = (computerIds ?? Array.Empty<Guid>()).ToArray();
        }
    }

    /// <summary>Describes one observed detectoid revision.</summary>
    public sealed class ObservedDetectoidObservation
    {
        /// <summary>Gets the detectoid identifier.</summary>
        public Guid UpdateId { get; }

        /// <summary>Gets the revision number.</summary>
        public int RevisionNumber { get; }

        /// <summary>Gets when the detectoid was first observed.</summary>
        public DateTimeOffset FirstSeen { get; }

        /// <summary>Gets when the detectoid was last observed.</summary>
        public DateTimeOffset LastSeen { get; }

        /// <summary>Gets the number of requests in which it was observed.</summary>
        public long ObservationCount { get; }

        /// <summary>Creates an observed detectoid value.</summary>
        public ObservedDetectoidObservation(
            Guid updateId,
            int revisionNumber,
            DateTimeOffset firstSeen,
            DateTimeOffset lastSeen,
            long observationCount)
        {
            UpdateId = updateId;
            RevisionNumber = revisionNumber;
            FirstSeen = firstSeen;
            LastSeen = lastSeen;
            ObservationCount = observationCount;
        }
    }

    /// <summary>Describes one observed string identifier.</summary>
    public sealed class ObservedIdentifierObservation
    {
        /// <summary>Gets the normalized identifier.</summary>
        public string Identifier { get; }

        /// <summary>Gets when the identifier was first observed.</summary>
        public DateTimeOffset FirstSeen { get; }

        /// <summary>Gets when the identifier was last observed.</summary>
        public DateTimeOffset LastSeen { get; }

        /// <summary>Gets the number of requests in which it was observed.</summary>
        public long ObservationCount { get; }

        /// <summary>Creates an observed identifier value.</summary>
        public ObservedIdentifierObservation(
            string identifier,
            DateTimeOffset firstSeen,
            DateTimeOffset lastSeen,
            long observationCount)
        {
            Identifier = identifier;
            FirstSeen = firstSeen;
            LastSeen = lastSeen;
            ObservationCount = observationCount;
        }
    }

    /// <summary>Describes one observed computer hardware identifier.</summary>
    public sealed class ObservedComputerIdObservation
    {
        /// <summary>Gets the computer hardware identifier.</summary>
        public Guid ComputerId { get; }

        /// <summary>Gets when the identifier was first observed.</summary>
        public DateTimeOffset FirstSeen { get; }

        /// <summary>Gets when the identifier was last observed.</summary>
        public DateTimeOffset LastSeen { get; }

        /// <summary>Gets the number of requests in which it was observed.</summary>
        public long ObservationCount { get; }

        /// <summary>Creates an observed computer identifier value.</summary>
        public ObservedComputerIdObservation(
            Guid computerId,
            DateTimeOffset firstSeen,
            DateTimeOffset lastSeen,
            long observationCount)
        {
            ComputerId = computerId;
            FirstSeen = firstSeen;
            LastSeen = lastSeen;
            ObservationCount = observationCount;
        }
    }

    /// <summary>
    /// Indicates how a detectoid-to-product mapping was derived from the cached
    /// Microsoft Update category metadata.
    /// </summary>
    [Flags]
    public enum DetectoidProductMappingSource
    {
        /// <summary>No mapping source.</summary>
        None = 0,

        /// <summary>The detectoid directly declares the product as a category.</summary>
        DetectoidCategory = 1,

        /// <summary>The concrete product directly declares the detectoid as a prerequisite.</summary>
        ProductPrerequisite = 2,

        /// <summary>The detectoid declares a product family/company category whose nearest descendant is a concrete product.</summary>
        CategoryHierarchy = 4,

        /// <summary>The concrete product reaches the detectoid through one or more intermediate detectoids.</summary>
        TransitiveProductPrerequisite = 8,

        /// <summary>WUA directly reported a product-category node as installed/applicable.</summary>
        ObservedProductCategory = 16
    }

    /// <summary>Maps one detectoid GUID to one concrete Microsoft Update product category.</summary>
    public sealed class DetectoidProductMapping
    {
        /// <summary>Gets the detectoid GUID.</summary>
        public Guid DetectoidUpdateId { get; }

        /// <summary>Gets the concrete product category GUID.</summary>
        public Guid ProductCategoryId { get; }

        /// <summary>Gets how the mapping was discovered.</summary>
        public DetectoidProductMappingSource Source { get; }

        /// <summary>Creates a detectoid-to-product mapping.</summary>
        public DetectoidProductMapping(
            Guid detectoidUpdateId,
            Guid productCategoryId,
            DetectoidProductMappingSource source)
        {
            DetectoidUpdateId = detectoidUpdateId;
            ProductCategoryId = productCategoryId;
            Source = source;
        }
    }

    /// <summary>Describes a product category resolved from recent detectoid observations.</summary>
    public sealed class ObservedProductCategory
    {
        /// <summary>Gets the concrete product category GUID.</summary>
        public Guid ProductCategoryId { get; }

        /// <summary>Gets the most recent contributing detectoid observation.</summary>
        public DateTimeOffset LastSeen { get; }

        /// <summary>Gets the number of distinct observed detectoids mapped to this product.</summary>
        public long DetectoidCount { get; }

        /// <summary>Gets the sum of contributing detectoid observation counters.</summary>
        public long ObservationCount { get; }

        /// <summary>Creates a resolved observed product.</summary>
        public ObservedProductCategory(
            Guid productCategoryId,
            DateTimeOffset lastSeen,
            long detectoidCount,
            long observationCount)
        {
            ProductCategoryId = productCategoryId;
            LastSeen = lastSeen;
            DetectoidCount = detectoidCount;
            ObservationCount = observationCount;
        }
    }

    /// <summary>Summarizes the cached detectoid-to-product map.</summary>
    public sealed class DetectoidProductMapStatus
    {
        /// <summary>Gets the number of mapping pairs.</summary>
        public long MappingCount { get; }

        /// <summary>Gets the number of distinct mapped detectoids.</summary>
        public long MappedDetectoidCount { get; }

        /// <summary>Gets the number of distinct mapped products.</summary>
        public long ProductCount { get; }

        /// <summary>Gets the number of active observed detectoids that have at least one product mapping.</summary>
        public long ActiveMappedDetectoidCount { get; }

        /// <summary>Gets the number of active observed detectoids that have no product mapping.</summary>
        public long ActiveUnmappedDetectoidCount { get; }

        /// <summary>Gets when the map was last rebuilt.</summary>
        public DateTimeOffset? RebuiltAt { get; }

        /// <summary>Creates mapping status information.</summary>
        public DetectoidProductMapStatus(
            long mappingCount,
            long mappedDetectoidCount,
            long productCount,
            long activeMappedDetectoidCount,
            long activeUnmappedDetectoidCount,
            DateTimeOffset? rebuiltAt)
        {
            MappingCount = mappingCount;
            MappedDetectoidCount = mappedDetectoidCount;
            ProductCount = productCount;
            ActiveMappedDetectoidCount = activeMappedDetectoidCount;
            ActiveUnmappedDetectoidCount = activeUnmappedDetectoidCount;
            RebuiltAt = rebuiltAt;
        }
    }

    /// <summary>Summarizes observations for one kind of value.</summary>
    public sealed class ObservedInventoryKindStatus
    {
        /// <summary>Gets the total number of stored values.</summary>
        public long TotalCount { get; }

        /// <summary>Gets the number selected by the active time window.</summary>
        public long SelectedCount { get; }

        /// <summary>Gets the earliest stored observation timestamp.</summary>
        public DateTimeOffset? FirstSeen { get; }

        /// <summary>Gets the latest stored observation timestamp.</summary>
        public DateTimeOffset? LastSeen { get; }

        /// <summary>Creates a kind status value.</summary>
        public ObservedInventoryKindStatus(
            long totalCount,
            long selectedCount,
            DateTimeOffset? firstSeen,
            DateTimeOffset? lastSeen)
        {
            TotalCount = totalCount;
            SelectedCount = selectedCount;
            FirstSeen = firstSeen;
            LastSeen = lastSeen;
        }
    }

    /// <summary>Summarizes the observed inventory store.</summary>
    public sealed class ObservedInventoryStatus
    {
        /// <summary>Gets the active lower timestamp bound.</summary>
        public DateTimeOffset? SeenSince { get; }

        /// <summary>Gets detectoid status.</summary>
        public ObservedInventoryKindStatus Detectoids { get; }

        /// <summary>Gets PnP hardware identifier status.</summary>
        public ObservedInventoryKindStatus PnpHardwareIds { get; }

        /// <summary>Gets compatible identifier status.</summary>
        public ObservedInventoryKindStatus CompatibleIds { get; }

        /// <summary>Gets computer hardware identifier status.</summary>
        public ObservedInventoryKindStatus ComputerIds { get; }

        /// <summary>Creates observed inventory status.</summary>
        public ObservedInventoryStatus(
            DateTimeOffset? seenSince,
            ObservedInventoryKindStatus detectoids,
            ObservedInventoryKindStatus pnpHardwareIds,
            ObservedInventoryKindStatus compatibleIds,
            ObservedInventoryKindStatus computerIds)
        {
            SeenSince = seenSince?.ToUniversalTime();
            Detectoids = detectoids;
            PnpHardwareIds = pnpHardwareIds;
            CompatibleIds = compatibleIds;
            ComputerIds = computerIds;
        }
    }

    /// <summary>
    /// Optional capability implemented by a metadata store that records the
    /// fleet-wide union of product detectoids and hardware identifiers observed
    /// during Windows Update client scans.
    /// </summary>
    public interface IObservedInventoryStore
    {
        /// <summary>Records one request's observations.</summary>
        void RecordObservedInventory(ObservedInventoryBatch observations);

        /// <summary>Gets observed detectoids.</summary>
        IReadOnlyList<ObservedDetectoidObservation> GetObservedDetectoids(DateTimeOffset? seenSince = null);

        /// <summary>Gets observed PnP hardware identifiers.</summary>
        IReadOnlyList<ObservedIdentifierObservation> GetObservedPnpHardwareIds(DateTimeOffset? seenSince = null);

        /// <summary>Gets observed compatible identifiers.</summary>
        IReadOnlyList<ObservedIdentifierObservation> GetObservedCompatibleIds(DateTimeOffset? seenSince = null);

        /// <summary>Gets observed computer hardware identifiers.</summary>
        IReadOnlyList<ObservedComputerIdObservation> GetObservedComputerIds(DateTimeOffset? seenSince = null);

        /// <summary>Gets observed inventory status.</summary>
        ObservedInventoryStatus GetObservedInventoryStatus(DateTimeOffset? seenSince = null);

        /// <summary>Replaces the derived detectoid-to-product map atomically.</summary>
        void ReplaceDetectoidProductMappings(
            IEnumerable<DetectoidProductMapping> mappings,
            DateTimeOffset rebuiltAt);

        /// <summary>Gets concrete products resolved from observed detectoids.</summary>
        IReadOnlyList<ObservedProductCategory> GetObservedProductCategories(DateTimeOffset? seenSince = null);

        /// <summary>Gets status for the derived detectoid-to-product map.</summary>
        DetectoidProductMapStatus GetDetectoidProductMapStatus(DateTimeOffset? seenSince = null);

        /// <summary>Deletes observations older than the supplied timestamp.</summary>
        long PruneObservedInventory(DateTimeOffset olderThan);
    }
}
