// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Drivers;
using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Stage of the WSUS client software discovery graph queried directly from SQLite.
    /// </summary>
    public enum ClientSyncSoftwareStage
    {
        Root = 0,
        NonLeaf = 1,
        BundledLeaf = 2,
        Leaf = 3
    }

    /// <summary>
    /// A published package record returned to one client request. The store may
    /// retain the underlying package in its bounded metadata cache.
    /// </summary>
    public sealed class ClientSyncPackageRecord
    {
        public int RevisionId { get; }
        public MicrosoftUpdatePackage Package { get; }
        public bool IsBundle { get; }
        public bool IsBundled { get; }

        public ClientSyncPackageRecord(
            int revisionId,
            MicrosoftUpdatePackage package,
            bool isBundle,
            bool isBundled)
        {
            RevisionId = revisionId;
            Package = package ?? throw new ArgumentNullException(nameof(package));
            IsBundle = isBundle;
            IsBundled = isBundled;
        }
    }

    /// <summary>
    /// A content digest and its directly downloadable upstream URL.
    /// </summary>
    public sealed class ClientSyncFileLocationRecord
    {
        public byte[] Digest { get; }
        public string Url { get; }

        public ClientSyncFileLocationRecord(byte[] digest, string url)
        {
            Digest = digest ?? throw new ArgumentNullException(nameof(digest));
            Url = url ?? throw new ArgumentNullException(nameof(url));
        }
    }

    /// <summary>
    /// Direct SQLite read model used by the client-facing WSUS endpoint.
    /// Implementations may retain a bounded cache of selected package metadata, but
    /// the complete catalog and dependency graph remain materialized in SQLite.
    /// </summary>
    public interface IClientSyncMetadataStore : IDisposable
    {
        /// <summary>Records one client scan's observations locally.</summary>
        void RecordObservedInventory(ObservedInventoryBatch observations);

        /// <summary>Returns the currently published catalog generation.</summary>
        MetadataStoreGenerationInfo GetPublishedCatalogInfo();

        /// <summary>Resolves published local revision IDs to their global identities in SQL batches.</summary>
        IReadOnlyDictionary<int, MicrosoftUpdatePackageIdentity> GetPackageIdentities(
            IEnumerable<int> revisionIds);

        /// <summary>
        /// Resolves published local revision IDs that can describe installed products:
        /// detectoids and Microsoft Update product-category nodes.
        /// </summary>
        IReadOnlyDictionary<int, MicrosoftUpdatePackageIdentity> GetObservedDetectorIdentities(
            IEnumerable<int> revisionIds);

        /// <summary>Resolves one published local revision ID to its global identity.</summary>
        bool TryGetPackageIdentity(int revisionId, out MicrosoftUpdatePackageIdentity identity);

        /// <summary>Resolves one published local revision ID only when it is a detectoid.</summary>
        bool TryGetDetectoidIdentity(int revisionId, out MicrosoftUpdatePackageIdentity identity);

        /// <summary>Loads one published package by local revision ID.</summary>
        bool TryGetPackage(int revisionId, out ClientSyncPackageRecord package);

        /// <summary>Loads one published package by global identity.</summary>
        bool TryGetPackage(MicrosoftUpdatePackageIdentity identity, out ClientSyncPackageRecord package);

        /// <summary>Gets the published local revision ID for an identity, or -1.</summary>
        int GetRevisionId(MicrosoftUpdatePackageIdentity identity);

        /// <summary>Reads the raw metadata XML for a published package.</summary>
        Stream GetMetadata(MicrosoftUpdatePackageIdentity identity);

        /// <summary>Reads file metadata and upstream URLs for a published package.</summary>
        IReadOnlyList<UpdateFile> GetFiles(MicrosoftUpdatePackageIdentity identity);

        /// <summary>
        /// Selects the next software discovery response directly from SQLite.
        /// Applicability and approvals are evaluated in SQL, and only the returned
        /// packages are materialized.
        /// </summary>
        IReadOnlyList<ClientSyncPackageRecord> GetSoftwareCandidates(
            ClientSyncSoftwareStage stage,
            IReadOnlyCollection<Guid> installedNonLeafUpdateIds,
            IReadOnlyCollection<Guid> otherCachedUpdateIds,
            bool approveAllSoftwareUpdates,
            IReadOnlyCollection<MicrosoftUpdatePackageIdentity> approvedSoftwareUpdates,
            int maxResults,
            out bool truncated);

        /// <summary>
        /// Returns whether a later software-discovery stage still contains a
        /// published candidate. Applicability is intentionally not evaluated because
        /// the current response can make later graph levels applicable on the next call.
        /// </summary>
        bool HasLaterSoftwareCandidates(
            ClientSyncSoftwareStage currentStage,
            IReadOnlyCollection<Guid> installedNonLeafUpdateIds,
            IReadOnlyCollection<Guid> otherCachedUpdateIds,
            bool approveAllSoftwareUpdates,
            IReadOnlyCollection<MicrosoftUpdatePackageIdentity> approvedSoftwareUpdates);

        /// <summary>Finds the best published driver for a device directly from SQLite.</summary>
        DriverMatchResult MatchDriver(
            IEnumerable<string> hardwareIds,
            IEnumerable<Guid> computerHardwareIds,
            IReadOnlyCollection<Guid> installedPrerequisites);

        /// <summary>Resolves requested SHA-1 digests to URLs from published packages.</summary>
        IReadOnlyList<ClientSyncFileLocationRecord> GetFileLocations(IEnumerable<byte[]> sha1Digests);
    }
}
