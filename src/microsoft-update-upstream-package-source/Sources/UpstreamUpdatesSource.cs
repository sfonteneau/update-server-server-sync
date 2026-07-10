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
        /// Breaks down a flat list of objects in a list of batches, each batch having a maximum allowed size
        /// </summary>
        /// <typeparam name="T">The type of objects to batch</typeparam>
        /// <param name="flatList">The flat list of objects to break down</param>
        /// <param name="maxBatchSize">The maximum size of a batch</param>
        /// <returns>The batched list</returns>
        private static List<List<T>> CreateBatchedListFromFlatList<T>(List<T> flatList, int maxBatchSize)
        {
            maxBatchSize = Math.Max(1, maxBatchSize);

            // Figure out how many batches we have
            var batchCount = flatList.Count / maxBatchSize;
            // One more batch for the remaining objects, if any
            batchCount += flatList.Count % maxBatchSize == 0 ? 0 : 1;

            List<List<T>> batches = new(batchCount);
            for (int i = 0; i < batchCount; i++)
            {
                var batchSize = maxBatchSize;
                // If this is the last batch, the size might not be the max allowed size but the remainder of elements
                if (i == batchCount - 1 && flatList.Count % maxBatchSize != 0)
                {
                    batchSize = flatList.Count % maxBatchSize;
                }

                // Add the new batch to the batches list
                batches.Add(flatList.GetRange(i * maxBatchSize, batchSize));
            }

            return batches;
        }

        /// <inheritdoc cref="IMetadataSource.CopyTo(IMetadataSink, CancellationToken)"/>
        public void CopyTo(IMetadataSink destination, CancellationToken cancelToken)
        {
            RetrievePackageIdentities();

            List<MicrosoftUpdatePackageIdentity> unavailableUpdates;

            if (destination is IMetadataStore destinationBaseline)
            {
                 unavailableUpdates = _Identities.Where(u => !destinationBaseline.ContainsPackage(u)).ToList();
            }
            else
            {
                unavailableUpdates = _Identities;
            }

            var progressArgs = new PackageStoreEventArgs() { Total = unavailableUpdates.Count, Current = 0 };
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
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }

                var retrievedPackages = _Client.GetUpdateDataForIds(batch.ToList(), _Filter);
                destination.AddPackages(retrievedPackages);
                retrievedPackages.ForEach(u => u.ReleaseMetadataBytes());

                progressArgs.Current += retrievedPackages.Count;
                MetadataCopyProgress?.Invoke(this, progressArgs);
            }
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
