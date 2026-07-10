// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Creates a local SQLite metadata store using the relational Microsoft Update schema.
    ///
    /// This backend intentionally has no compatibility or migration path for the historical
    /// zip-delta store or for earlier SQLite layouts. Delete metadata.sqlite before deploying
    /// a schema-changing build.
    /// </summary>
    public abstract class PackageStore
    {
        public static IMetadataStore Open(string path)
        {
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(path);
            }

            if (!SQLitePackageStore.Exists(path))
            {
                throw new DirectoryNotFoundException($"No relational SQLite metadata store found in {path}");
            }

            return SQLitePackageStore.OpenExisting(path);
        }

        public static IMetadataStore OpenOrCreate(string path)
        {
            return SQLitePackageStore.OpenOrCreate(path);
        }

        public static bool Exists(string path)
        {
            return SQLitePackageStore.Exists(path);
        }

        public static void OptimizeSQLiteStore(
            string path,
            bool replaceDatabaseFile,
            bool rebuildIndexes,
            Action<string> log = null)
        {
            if (!Directory.Exists(path) || !SQLitePackageStore.Exists(path))
            {
                throw new DirectoryNotFoundException($"No relational SQLite metadata store found in {path}");
            }

            SQLitePackageStore.OptimizeExisting(path, replaceDatabaseFile, rebuildIndexes, log);
        }
    }
}
