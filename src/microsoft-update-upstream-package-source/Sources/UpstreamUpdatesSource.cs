// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Source
{
    /// <summary>
    /// Retrieves updates from the Microsoft Update catalog or a WSUS upstream server.
    /// </summary>
    public class UpstreamUpdatesSource : IMetadataSource
    {
        private readonly UpstreamServerClient _Client;
        private readonly string _OldAnchor;
        private UpstreamSourceFilter _Filter;

        private List<MicrosoftUpdatePackageIdentity> _Identities;

        /// <summary>
        /// Anchor returned by the upstream server for the last GetRevisionIdList request.
        /// Persist it only after the retrieved metadata has been written successfully.
        /// </summary>
        public string NewAnchor { get; private set; }

        /// <summary>
        /// Client-side metadata retrieval batch size. This is independent from the
        /// upstream MaxNumberOfUpdatesPerRequest limit; the client will split again
        /// if the service reports a smaller limit.
        /// </summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>
        /// Progress indicator during metadata copy operations
        /// </summary>
        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;

#pragma warning disable 0067
        /// <summary>
        /// Progress indicator during source open operations. Not used by UpstreamUpdatesSource.
        /// </summary>
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
#pragma warning restore 0067

        /// <summary>
        /// Create a new MicrosoftUpdate package source that retrieves updates from the specified endpoint
        /// </summary>
        /// <param name="upstreamEndpoint">Endpoint to get updates from</param>
        /// <param name="filter">Filter to apply when retrieving updates from this source.</param>
        public UpstreamUpdatesSource(Endpoint upstreamEndpoint, UpstreamSourceFilter filter)
            : this(upstreamEndpoint, filter, null)
        {
        }

        /// <summary>
        /// Create a new MicrosoftUpdate package source using an optional upstream anchor.
        /// </summary>
        /// <param name="upstreamEndpoint">Endpoint to get updates from</param>
        /// <param name="filter">Filter to apply when retrieving updates from this source.</param>
        /// <param name="oldAnchor">Previously saved WSUS anchor for this exact endpoint/filter.</param>
        public UpstreamUpdatesSource(Endpoint upstreamEndpoint, UpstreamSourceFilter filter, string oldAnchor)
        {
            _Client = new UpstreamServerClient(upstreamEndpoint);
            _Filter = filter;
            _OldAnchor = oldAnchor;
        }

        private void RetrievePackageIdentities()
        {
            lock (this)
            {
                if (_Identities == null)
                {
                    _Identities = _Client
                        .GetUpdateIds(_Filter, out var newAnchor, _OldAnchor)
                        .Distinct()
                        .ToList();
                    _Identities.Sort();
                    NewAnchor = newAnchor;
                }
            }
        }

        /// <summary>
        /// Retrieves and caches the complete revision identity list returned by
        /// GetRevisionIdList for this source and anchor.
        /// </summary>
        public IReadOnlyList<MicrosoftUpdatePackageIdentity> GetPackageIdentities()
        {
            RetrievePackageIdentities();
            return _Identities.ToList();
        }

        /// <summary>
        /// Breaks down a flat list of objects in a list of batches, each batch having a maximum allowed size
        /// </summary>
        /// <typeparam name="T">The type of objects to batch</typeparam>
        /// <param name="flatList">The flat list of objects to break down</param>
        /// <param name="maxBatchSize">The maximum size of a batch</param>
        /// <returns>The batched list</returns>
        private static List<List<T>> CreateBatchedListFromFlatList<T>(List<T> flatList, int maxBatchSize)
        {
            maxBatchSize = Math.Max(1, maxBatchSize);

            var batchCount = flatList.Count / maxBatchSize;
            batchCount += flatList.Count % maxBatchSize == 0 ? 0 : 1;

            List<List<T>> batches = new(batchCount);
            for (int i = 0; i < batchCount; i++)
            {
                var batchSize = maxBatchSize;
                if (i == batchCount - 1 && flatList.Count % maxBatchSize != 0)
                {
                    batchSize = flatList.Count % maxBatchSize;
                }

                batches.Add(flatList.GetRange(i * maxBatchSize, batchSize));
            }

            return batches;
        }

        /// <summary>
        /// Retrieves and stores only the supplied revision identities. The callback
        /// is invoked after each local batch has been committed to the destination.
        /// This allows callers to checkpoint progress without advancing the WSUS anchor.
        /// </summary>
        public void CopyPackagesTo(
            IMetadataSink destination,
            IEnumerable<MicrosoftUpdatePackageIdentity> packageIdentities,
            CancellationToken cancelToken,
            Action<IReadOnlyList<MicrosoftUpdatePackageIdentity>> batchStored = null)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            var requestedUpdates = (packageIdentities ?? Array.Empty<MicrosoftUpdatePackageIdentity>())
                .Where(identity => identity != null)
                .Distinct()
                .ToList();

            var destinationBaseline = destination as IMetadataStore;
            var alreadyAvailable = destinationBaseline == null
                ? new List<MicrosoftUpdatePackageIdentity>()
                : requestedUpdates.Where(destinationBaseline.ContainsPackage).ToList();

            if (alreadyAvailable.Count > 0)
            {
                batchStored?.Invoke(alreadyAvailable);
            }

            var alreadyAvailableSet = new HashSet<MicrosoftUpdatePackageIdentity>(alreadyAvailable);
            var unavailableUpdates = requestedUpdates
                .Where(identity => !alreadyAvailableSet.Contains(identity))
                .ToList();

            var progressArgs = new PackageStoreEventArgs
            {
                Total = requestedUpdates.Count,
                Current = alreadyAvailable.Count
            };
            MetadataCopyProgress?.Invoke(this, progressArgs);

            if (unavailableUpdates.Count == 0)
            {
                return;
            }

            var batches = CreateBatchedListFromFlatList(unavailableUpdates, BatchSize);

            // Keep write operations sequential. SQLite has a single writer and the store also updates
            // in-memory indexes; firing many concurrent AddPackages calls mostly creates lock contention.
            foreach (var batch in batches)
            {
                cancelToken.ThrowIfCancellationRequested();

                var retrievedPackages = _Client.GetUpdateDataForIds(batch, _Filter);
                try
                {
                    destination.AddPackages(retrievedPackages);

                    List<MicrosoftUpdatePackageIdentity> completedBatch;
                    if (destinationBaseline != null)
                    {
                        completedBatch = batch
                            .Where(destinationBaseline.ContainsPackage)
                            .ToList();
                    }
                    else
                    {
                        var batchSet = new HashSet<MicrosoftUpdatePackageIdentity>(batch);
                        completedBatch = retrievedPackages
                            .Select(package => package.Id)
                            .OfType<MicrosoftUpdatePackageIdentity>()
                            .Where(batchSet.Contains)
                            .Distinct()
                            .ToList();
                    }

                    if (completedBatch.Count > 0)
                    {
                        batchStored?.Invoke(completedBatch);
                    }

                    progressArgs.Current += completedBatch.Count;
                    MetadataCopyProgress?.Invoke(this, progressArgs);

                    var completedSet = new HashSet<MicrosoftUpdatePackageIdentity>(completedBatch);
                    var missingUpdates = batch
                        .Where(identity => !completedSet.Contains(identity))
                        .ToList();

                    if (missingUpdates.Count > 0)
                    {
                        var sample = string.Join(", ", missingUpdates.Take(10));
                        throw new InvalidDataException(
                            $"The upstream server did not return metadata for {missingUpdates.Count} requested revision(s). " +
                            $"The synchronization anchor was not advanced. Missing sample: {sample}");
                    }
                }
                finally
                {
                    retrievedPackages.ForEach(package => package.ReleaseMetadataBytes());
                }
            }
        }

        /// <inheritdoc cref="IMetadataSource.CopyTo(IMetadataSink, CancellationToken)"/>
        public void CopyTo(IMetadataSink destination, CancellationToken cancelToken)
        {
            CopyPackagesTo(destination, GetPackageIdentities(), cancelToken);
        }

        /// <inheritdoc cref="IMetadataSource.CopyTo(IMetadataSink, IMetadataFilter, CancellationToken)"/>
        public void CopyTo(IMetadataSink destination, IMetadataFilter filter, CancellationToken cancelToken)
        {
            if (filter is UpstreamSourceFilter categoriesFilter)
            {
                _Filter = categoriesFilter;
            }

            CopyTo(destination, cancelToken);
        }

        /// <summary>
        /// Not implemented for an upstream update source
        /// </summary>
        /// <param name="packageIdentity">Identity of update to retrieve</param>
        /// <returns>Update metadata as stream</returns>
        /// <exception cref="NotImplementedException"></exception>
        public Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented for an upstream update source
        /// </summary>
        /// <param name="packageIdentity">Indentity of package to lookup</param>
        /// <returns>True if found, false otherwise</returns>
        /// <exception cref="NotImplementedException"></exception>
        public bool ContainsMetadata(IPackageIdentity packageIdentity)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented for an upstream update source
        /// </summary>
        /// <typeparam name="T">Type of file to retrieve.</typeparam>
        /// <param name="packageIdentity">Identity of the package to retrieve files for.</param>
        /// <returns>List of files in the package</returns>
        /// <exception cref="NotImplementedException"></exception>
        public List<T> GetFiles<T>(IPackageIdentity packageIdentity)
        {
            throw new NotImplementedException();
        }
    }
}
