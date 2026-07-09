// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Creates an instance of <see cref="IMetadataStore"/> that stores update metadata locally in a specified directory.
    ///
    /// The default local store is SQLite-backed and writes store/metadata.sqlite. Legacy zip-delta
    /// stores are migrated automatically on first open, then moved aside to a backup directory.
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

            MigrateLegacyZipStoreIfNeeded(path);

            if (!SQLitePackageStore.Exists(path))
            {
                throw new DirectoryNotFoundException($"No SQLite metadata store found in {path}");
            }

            return SQLitePackageStore.OpenExisting(path);
        }

        /// <summary>
        /// Opens an existing IMetadataStore from the specified directory. If a store does not exist,
        /// or the directory does not exist, a new SQLite store is created.
        /// </summary>
        /// <param name="path">Path to the directory to open or create</param>
        /// <returns>An instance of IMetadataStore</returns>
        public static IMetadataStore OpenOrCreate(string path)
        {
            if (Directory.Exists(path))
            {
                MigrateLegacyZipStoreIfNeeded(path);
            }

            return SQLitePackageStore.OpenOrCreate(path);
        }

        /// <summary>
        /// Checks if a IMetadataStore exists in the specified directory.
        /// </summary>
        /// <param name="path">Path to the directory to check.</param>
        /// <returns>True if a store exists under the directory, false otherwise</returns>
        public static bool Exists(string path)
        {
            return SQLitePackageStore.Exists(path) || DirectoryPackageStore.Exists(path);
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

            MigrateLegacyZipStoreIfNeeded(path);

            if (!SQLitePackageStore.Exists(path))
            {
                throw new DirectoryNotFoundException($"No SQLite metadata store found in {path}");
            }

            SQLitePackageStore.OptimizeExisting(path, replaceDatabaseFile, rebuildIndexes, log);
        }

        private static void MigrateLegacyZipStoreIfNeeded(string path)
        {
            if (SQLitePackageStore.Exists(path) || !DirectoryPackageStore.Exists(path))
            {
                return;
            }

            var databasePath = Path.Combine(path, SQLitePackageStore.DatabaseFileName);
            var failedDatabasePath = databasePath + ".failed";

            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }

            using (var legacyStore = new DirectoryPackageStore(path, FileMode.Open))
            using (var sqliteStore = SQLitePackageStore.OpenOrCreate(path))
            {
                if (legacyStore.IsReindexingRequired)
                {
                    legacyStore.ReIndex();
                    legacyStore.Flush();
                }

                legacyStore.CopyTo(sqliteStore, CancellationToken.None);
                sqliteStore.Flush();
            }

            if (!SQLitePackageStore.Exists(path))
            {
                if (File.Exists(databasePath))
                {
                    File.Move(databasePath, failedDatabasePath, true);
                }

                throw new InvalidDataException("Legacy zip metadata store migration failed");
            }

            MoveLegacyZipFilesToBackup(path);
        }

        private static void MoveLegacyZipFilesToBackup(string path)
        {
            var backupDirectory = Path.Combine(path, "legacy-zip-store-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(backupDirectory);

            foreach (var file in Directory.GetFiles(path, "*.zip", SearchOption.TopDirectoryOnly))
            {
                MovePath(file, Path.Combine(backupDirectory, Path.GetFileName(file)));
            }

            foreach (var legacyFileName in new[] { ".toc.json", ".types.json" })
            {
                var legacyFile = Path.Combine(path, legacyFileName);
                if (File.Exists(legacyFile))
                {
                    MovePath(legacyFile, Path.Combine(backupDirectory, legacyFileName));
                }
            }

            var identitiesDirectory = Path.Combine(path, "identities");
            if (Directory.Exists(identitiesDirectory))
            {
                MovePath(identitiesDirectory, Path.Combine(backupDirectory, "identities"));
            }

            if (!Directory.EnumerateFileSystemEntries(backupDirectory).Any())
            {
                Directory.Delete(backupDirectory);
            }
        }

        private static void MovePath(string source, string destination)
        {
            if (File.Exists(source))
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                File.Move(source, destination);
            }
            else if (Directory.Exists(source))
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, true);
                }

                Directory.Move(source, destination);
            }
        }
    }
}
