// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Creates an instance of <see cref="IMetadataStore"/> that stores update metadata locally in a specified directory.
    ///
    /// The local store is SQLite-backed and writes store/metadata.sqlite.
    /// Legacy zip stores and older SQLite schemas are intentionally not migrated.
    /// </summary>
    public abstract class PackageStore
    {
        /// <summary>
        /// Opens an existing IMetadataStore from the specified directory.
        /// </summary>
        /// <param name="path">Path to the directory containing the IMetadataStore to open.</param>
        /// <returns>An instance of IMetadataStore</returns>
        /// <exception cref="DirectoryNotFoundException">If the directory does not exist or does not contain a valid IMetadataStore.</exception>
        public static IMetadataStore Open(string path)
        {
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(path);
            }

            if (!SQLitePackageStore.Exists(path))
            {
                throw new DirectoryNotFoundException($"No SQLite metadata store found in {path}");
            }

            return SQLitePackageStore.OpenExisting(path);
        }


        /// <summary>
        /// Opens the direct SQLite read model used by the client-facing update server.
        /// The returned store performs SQL reads per request and does not load the
        /// package catalog or metadata indexes into application memory.
        /// </summary>
        /// <param name="path">Path containing metadata.sqlite.</param>
        /// <returns>A direct client-sync metadata store.</returns>
        public static IClientSyncMetadataStore OpenClientSync(string path)
        {
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(path);
            }

            if (!SQLitePackageStore.Exists(path))
            {
                throw new DirectoryNotFoundException($"No SQLite metadata store found in {path}");
            }

            return new SQLiteClientSyncMetadataStore(path);
        }

        /// <summary>
        /// Opens an existing IMetadataStore from the specified directory. If a store does not exist,
        /// or the directory does not exist, a new SQLite store is created.
        /// </summary>
        /// <param name="path">Path to the directory to open or create</param>
        /// <returns>An instance of IMetadataStore</returns>
        public static IMetadataStore OpenOrCreate(string path)
        {
            return SQLitePackageStore.OpenOrCreate(path);
        }

        /// <summary>
        /// Checks if a IMetadataStore exists in the specified directory.
        /// </summary>
        /// <param name="path">Path to the directory to check.</param>
        /// <returns>True if a store exists under the directory, false otherwise</returns>
        public static bool Exists(string path)
        {
            return SQLitePackageStore.Exists(path);
        }

        /// <summary>
        /// Optimizes a local SQLite package store by compressing old uncompressed rows,
        /// rebuilding compact lookup tables and optionally replacing metadata.sqlite with
        /// a VACUUM INTO compact copy.
        /// </summary>
        /// <param name="path">Path to the local package store directory.</param>
        /// <param name="replaceDatabaseFile">When true, replaces metadata.sqlite with a compact copy and keeps the previous file as a backup.</param>
        /// <param name="rebuildIndexes">When true, rebuilds indexes. This also drops the old full file-location index payload.</param>
        /// <param name="log">Optional progress callback.</param>
        public static void OptimizeSQLiteStore(string path, bool replaceDatabaseFile, bool rebuildIndexes, Action<string> log = null)
        {
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(path);
            }

            if (!SQLitePackageStore.Exists(path))
            {
                throw new DirectoryNotFoundException($"No SQLite metadata store found in {path}");
            }

            SQLitePackageStore.OptimizeExisting(path, replaceDatabaseFile, rebuildIndexes, log);
        }

    }
}
