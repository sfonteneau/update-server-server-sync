// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate.Index;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using Microsoft.PackageGraph.Storage.Index;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// SQLite-backed local metadata store.
    ///
    /// This replaces the historical local store layout that created append-only
    /// delta archives named 0.zip, 1.zip, ... . Metadata, file metadata and the
    /// serialized index container now live in store/metadata.sqlite.
    /// </summary>
    class SQLitePackageStore : IMetadataSink, IMetadataStore, IMetadataLookup, IMicrosoftUpdateFileLocationLookup
    {
        public const string DatabaseFileName = "metadata.sqlite";

        private const int SchemaVersion = 2;
        private const string IndexBlobKey = "indexes.zip";
        private const string CompressionNone = "none";
        private const string CompressionBrotli = "br";
        private const string CompressionGZip = "gzip";
        private const int OptimizeBatchSize = 200;

        private readonly string TargetPath;
        private readonly string DatabasePath;
        private readonly SqliteConnection Connection;
        private readonly ReaderWriterLockSlim StateLock = new(LockRecursionPolicy.SupportsRecursion);

        private Dictionary<IPackageIdentity, int> _IdentityToIndexMap = new();
        private Dictionary<int, IPackageIdentity> _IndexToIdentityMap = new();
        private Dictionary<int, int> _PackageTypeIndex = new();

        private int _NextPackageIndex;
        private bool IsDirty;
        private bool IsIndexDirty;
        private bool IsDisposed;
        private bool _IsReindexingRequired;
        private bool _FileLocationFileJsonIsRequired;

        private MemoryStream IndexBackingStream;
        private ZipStreamIndexContainer Indexes;

        private readonly List<IPackage> PendingPackages = new();

#pragma warning disable 0067
        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
#pragma warning restore 0067
        public event EventHandler<PackageStoreEventArgs> PackagesAddProgress;
        public event EventHandler<PackageStoreEventArgs> PackageIndexingProgress;

        public int PackageCount => _IdentityToIndexMap.Count;

        /// <inheritdoc cref="IMetadataStore.IsReindexingRequired"/>
        public bool IsReindexingRequired => _IsReindexingRequired;

        /// <inheritdoc cref="IMetadataStore.IsMetadataIndexingSupported"/>
        public bool IsMetadataIndexingSupported { get; private set; } = true;

        private SQLitePackageStore(string path, FileMode mode, bool autoReindex = true)
        {
            TargetPath = path;
            DatabasePath = Path.Combine(TargetPath, DatabaseFileName);

            if (!Directory.Exists(TargetPath))
            {
                if (mode == FileMode.Open)
                {
                    throw new DirectoryNotFoundException(TargetPath);
                }

                Directory.CreateDirectory(TargetPath);
            }

            if (mode == FileMode.Open && !File.Exists(DatabasePath))
            {
                throw new FileNotFoundException("The SQLite metadata store does not exist", DatabasePath);
            }

            var isNewDatabase = !File.Exists(DatabasePath);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            Connection = new SqliteConnection(connectionString);
            Connection.Open();

            if (isNewDatabase)
            {
                ExecuteNonQuery("PRAGMA page_size=8192;");
                ExecuteNonQuery("PRAGMA auto_vacuum=INCREMENTAL;");
            }

            ExecuteNonQuery("PRAGMA journal_mode=WAL;");
            ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
            ExecuteNonQuery("PRAGMA foreign_keys=ON;");

            InitializeSchema();
            EnsureCompressionSchema();
            LoadPackageMaps();
            LoadIndexes();

            if (autoReindex && _IsReindexingRequired)
            {
                CheckIndex(true);
                Flush();
            }
        }

        public static SQLitePackageStore OpenExisting(string path)
        {
            return new SQLitePackageStore(path, FileMode.Open);
        }

        public static SQLitePackageStore OpenOrCreate(string path)
        {
            return new SQLitePackageStore(path, FileMode.OpenOrCreate);
        }

        public static bool Exists(string path)
        {
            return Directory.Exists(path) && File.Exists(Path.Combine(path, DatabaseFileName));
        }

        private void InitializeSchema()
        {
            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS store_properties (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS packages (
    package_index INTEGER PRIMARY KEY,
    partition TEXT NOT NULL,
    open_id_hex TEXT NOT NULL,
    identity TEXT NOT NULL,
    package_type INTEGER NOT NULL,
    metadata BLOB NOT NULL,
    metadata_compression TEXT NOT NULL DEFAULT 'br',
    files_json TEXT NULL,
    files_blob BLOB NULL,
    files_compression TEXT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(partition, open_id_hex),
    UNIQUE(identity)
);

CREATE INDEX IF NOT EXISTS idx_packages_identity
ON packages(identity);

CREATE INDEX IF NOT EXISTS idx_packages_partition_openid
ON packages(partition, open_id_hex);

CREATE TABLE IF NOT EXISTS store_blobs (
    key TEXT PRIMARY KEY,
    value BLOB NOT NULL
);

CREATE TABLE IF NOT EXISTS file_locations (
    sha1_base64 TEXT PRIMARY KEY,
    sha1 BLOB NOT NULL,
    sha1_hex TEXT NOT NULL,
    mu_url TEXT NULL,
    file_name TEXT NULL,
    file_json TEXT NULL,
    file_blob BLOB NULL,
    file_compression TEXT NULL,
    package_index INTEGER NOT NULL,
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_file_locations_package
ON file_locations(package_index);

CREATE TABLE IF NOT EXISTS package_file_map (
    package_index INTEGER NOT NULL,
    sha1_base64 TEXT NOT NULL,
    PRIMARY KEY(package_index, sha1_base64),
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE,
    FOREIGN KEY(sha1_base64) REFERENCES file_locations(sha1_base64) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_package_file_map_sha1
ON package_file_map(sha1_base64);
");

            using var command = Connection.CreateCommand();
            command.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ('schema_version', $schemaVersion)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$schemaVersion", SchemaVersion.ToString());
            command.ExecuteNonQuery();
        }

        private void EnsureCompressionSchema()
        {
            EnsureColumnExists("packages", "metadata_compression", "TEXT NOT NULL DEFAULT 'none'");
            EnsureColumnExists("packages", "files_blob", "BLOB NULL");
            EnsureColumnExists("packages", "files_compression", "TEXT NULL");
            EnsureColumnExists("file_locations", "file_blob", "BLOB NULL");
            EnsureColumnExists("file_locations", "file_compression", "TEXT NULL");

            ExecuteNonQuery(@"
INSERT OR IGNORE INTO package_file_map(package_index, sha1_base64)
SELECT package_index, sha1_base64
FROM file_locations
WHERE package_index IS NOT NULL;");

            _FileLocationFileJsonIsRequired = IsColumnNotNull("file_locations", "file_json");
        }

        private void EnsureColumnExists(string tableName, string columnName, string columnDefinition)
        {
            if (ColumnExists(tableName, columnName))
            {
                return;
            }

            ExecuteNonQuery($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
        }

        private bool ColumnExists(string tableName, string columnName)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsColumnNotNull(string tableName, string columnName)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return reader.GetInt32(3) != 0;
                }
            }

            return false;
        }

        private void LoadPackageMaps()
        {
            StateLock.EnterWriteLock();
            try
            {
                _IdentityToIndexMap = new Dictionary<IPackageIdentity, int>();
                _IndexToIdentityMap = new Dictionary<int, IPackageIdentity>();
                _PackageTypeIndex = new Dictionary<int, int>();

                var progressArgs = new PackageStoreEventArgs { Total = CountPackages(), Current = 0 };
                OpenProgress?.Invoke(this, progressArgs);

                using var command = Connection.CreateCommand();
                command.CommandText = @"
SELECT package_index, identity, package_type
FROM packages
ORDER BY package_index;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var packageIndex = reader.GetInt32(0);
                    var identityString = reader.GetString(1);
                    var packageType = reader.GetInt32(2);
                    var identity = IdentityFromString(identityString);

                    _IndexToIdentityMap.Add(packageIndex, identity);
                    _IdentityToIndexMap.Add(identity, packageIndex);
                    _PackageTypeIndex.Add(packageIndex, packageType);

                    progressArgs.Current++;
                    if (progressArgs.Current % 1000 == 0)
                    {
                        OpenProgress?.Invoke(this, progressArgs);
                    }
                }

                _NextPackageIndex = _IndexToIdentityMap.Count == 0 ? 0 : _IndexToIdentityMap.Keys.Max() + 1;
                OpenProgress?.Invoke(this, progressArgs);
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        private long CountPackages()
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM packages;";
            return (long)command.ExecuteScalar();
        }

        private static IPackageIdentity IdentityFromString(string identityString)
        {
            var separatorIndex = identityString.IndexOf(':');
            if (separatorIndex < 0)
            {
                throw new FormatException($"Invalid package identity string: {identityString}");
            }

            var partitionName = identityString.Substring(0, separatorIndex);
            if (PartitionRegistration.TryGetPartition(partitionName, out var partitionDefinition))
            {
                return partitionDefinition.Factory.IdentityFromString(identityString);
            }

            throw new NotImplementedException($"The package belongs to a partition that was not registered: {partitionName}");
        }

        private void LoadIndexes()
        {
            var indexData = ReadBlob(IndexBlobKey);
            if (indexData != null && indexData.Length > 0)
            {
                IndexBackingStream = new MemoryStream(indexData, false);
                Indexes = ZipStreamIndexContainer.Open(IndexBackingStream);
                if (Indexes.GetStatus() != ZipStreamIndexContainer.IndexContainerStatus.Valid)
                {
                    _IsReindexingRequired = true;
                }
            }
            else
            {
                Indexes = ZipStreamIndexContainer.Create();
                _IsReindexingRequired = _IdentityToIndexMap.Count > 0;
            }

            var indexedPackageCountString = ReadProperty("indexed_package_count");
            if (!int.TryParse(indexedPackageCountString, out var indexedPackageCount) ||
                indexedPackageCount != _IdentityToIndexMap.Count)
            {
                _IsReindexingRequired = _IdentityToIndexMap.Count > 0;
            }
        }

        private byte[] ReadBlob(string key)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "SELECT value FROM store_blobs WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? null : (byte[])value;
        }

        private string ReadProperty(string key)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "SELECT value FROM store_properties WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? null : (string)value;
        }

        private void WriteProperty(string key, string value)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ($key, $value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        private void WriteIndexes()
        {
            using var memoryStream = new MemoryStream();
            Indexes.Save(memoryStream);
            var indexBytes = memoryStream.ToArray();

            using var command = Connection.CreateCommand();
            command.CommandText = @"
INSERT INTO store_blobs(key, value)
VALUES ($key, $value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", IndexBlobKey);
            command.Parameters.AddWithValue("$value", indexBytes);
            command.ExecuteNonQuery();

            WriteProperty("indexed_package_count", _IdentityToIndexMap.Count.ToString());

            Indexes.CloseInput();
            IndexBackingStream?.Dispose();
            IndexBackingStream = new MemoryStream(indexBytes, false);
            Indexes = ZipStreamIndexContainer.Open(IndexBackingStream);
        }

        private void ExecuteNonQuery(string sql)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public bool ContainsPackage(IPackageIdentity packageIdentity)
        {
            StateLock.EnterReadLock();
            try
            {
                return _IdentityToIndexMap.ContainsKey(packageIdentity);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public bool ContainsMetadata(IPackageIdentity packageIdentity)
        {
            return ContainsPackage(packageIdentity);
        }

        public List<IPackageIdentity> GetPackageIdentities()
        {
            StateLock.EnterReadLock();
            try
            {
                return _IndexToIdentityMap
                    .OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value)
                    .ToList();
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public int GetPackageIndex(IPackageIdentity packageIdentity)
        {
            StateLock.EnterReadLock();
            try
            {
                return _IdentityToIndexMap.TryGetValue(packageIdentity, out var packageIndex) ? packageIndex : -1;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public IPackage GetPackage(IPackageIdentity packageIdentity)
        {
            StateLock.EnterReadLock();
            try
            {
                if (!_IdentityToIndexMap.TryGetValue(packageIdentity, out var packageIndex))
                {
                    throw new KeyNotFoundException();
                }

                return CreatePackageFromStore(packageIndex, packageIdentity);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public IPackage GetPackage(int packageIndex)
        {
            StateLock.EnterReadLock();
            try
            {
                if (!_IndexToIdentityMap.TryGetValue(packageIndex, out var packageIdentity))
                {
                    throw new KeyNotFoundException();
                }

                return CreatePackageFromStore(packageIndex, packageIdentity);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        private IPackage CreatePackageFromStore(int packageIndex, IPackageIdentity packageIdentity)
        {
            if (PartitionRegistration.TryGetPartitionFromPackageId(packageIdentity, out var partitionDefinition))
            {
                return partitionDefinition.Factory.FromStore(_PackageTypeIndex[packageIndex], packageIdentity, this, this);
            }

            throw new NotImplementedException($"The package belongs to a partition that was not registered: {packageIdentity.Partition}");
        }

        private IPackage CreatePackageFromStoredMetadata(int packageIndex)
        {
            var packageIdentity = _IndexToIdentityMap[packageIndex];
            if (PartitionRegistration.TryGetPartitionFromPackageId(packageIdentity, out var partitionDefinition))
            {
                var metadataBytes = ReadMetadataBytes(packageIndex);
                using var metadataStream = new MemoryStream(metadataBytes, false);
                return partitionDefinition.Factory.FromStream(metadataStream, this);
            }

            throw new NotImplementedException($"The package belongs to a partition that was not registered: {packageIdentity.Partition}");
        }

        public Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            StateLock.EnterReadLock();
            try
            {
                if (!_IdentityToIndexMap.TryGetValue(packageIdentity, out var packageIndex))
                {
                    throw new KeyNotFoundException();
                }

                return new MemoryStream(ReadMetadataBytes(packageIndex), false);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        private byte[] ReadMetadataBytes(int packageIndex)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = @"
SELECT metadata, COALESCE(metadata_compression, 'none')
FROM packages
WHERE package_index = $packageIndex;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new KeyNotFoundException();
            }

            var metadata = (byte[])reader[0];
            var compression = reader.IsDBNull(1) ? CompressionNone : reader.GetString(1);
            return DecompressBytes(metadata, compression);
        }

        public List<T> GetFiles<T>(IPackageIdentity packageIdentity)
        {
            StateLock.EnterReadLock();
            try
            {
                if (!_IdentityToIndexMap.TryGetValue(packageIdentity, out var packageIndex))
                {
                    throw new KeyNotFoundException();
                }

                var mappedFileJsonList = ReadMappedFileJsonList(packageIndex);
                if (mappedFileJsonList.Count > 0)
                {
                    return mappedFileJsonList
                        .Select(fileJson => JsonConvert.DeserializeObject<UpdateFile>(fileJson))
                        .Where(file => file != null)
                        .Cast<T>()
                        .ToList();
                }

                var filesJson = ReadFilesJson(packageIndex);
                if (string.IsNullOrEmpty(filesJson))
                {
                    return new List<T>();
                }

                return JsonConvert.DeserializeObject<List<T>>(filesJson) ?? new List<T>();
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        private List<string> ReadMappedFileJsonList(int packageIndex)
        {
            var filesJson = new List<string>();
            using var command = Connection.CreateCommand();
            command.CommandText = @"
SELECT fl.file_blob, fl.file_compression, fl.file_json
FROM package_file_map pfm
JOIN file_locations fl ON fl.sha1_base64 = pfm.sha1_base64
WHERE pfm.package_index = $packageIndex
ORDER BY pfm.sha1_base64;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    var blob = (byte[])reader[0];
                    var compression = reader.IsDBNull(1) ? CompressionNone : reader.GetString(1);
                    filesJson.Add(DecompressString(blob, compression));
                }
                else if (!reader.IsDBNull(2))
                {
                    filesJson.Add(reader.GetString(2));
                }
            }

            return filesJson;
        }

        private string ReadFilesJson(int packageIndex)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = @"
SELECT files_blob, files_compression, files_json
FROM packages
WHERE package_index = $packageIndex;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            if (!reader.IsDBNull(0))
            {
                var blob = (byte[])reader[0];
                var compression = reader.IsDBNull(1) ? CompressionNone : reader.GetString(1);
                return DecompressString(blob, compression);
            }

            return reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        public IReadOnlyList<UpdateFile> FindFilesBySha1(IEnumerable<byte[]> sha1Digests)
        {
            if (sha1Digests == null)
            {
                return Array.Empty<UpdateFile>();
            }

            var requested = sha1Digests
                .Where(digest => digest != null && digest.Length > 0)
                .Select(Convert.ToBase64String)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (requested.Count == 0)
            {
                return Array.Empty<UpdateFile>();
            }

            StateLock.EnterReadLock();
            try
            {
                var files = new List<UpdateFile>();
                using var command = Connection.CreateCommand();
                command.CommandText = @"
SELECT file_blob, file_compression, file_json
FROM file_locations
WHERE sha1_base64 = $sha1;";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$sha1";
                command.Parameters.Add(parameter);

                foreach (var digest in requested)
                {
                    parameter.Value = digest;
                    using var reader = command.ExecuteReader();
                    if (!reader.Read())
                    {
                        continue;
                    }

                    string fileJson;
                    if (!reader.IsDBNull(0))
                    {
                        var blob = (byte[])reader[0];
                        var compression = reader.IsDBNull(1) ? CompressionNone : reader.GetString(1);
                        fileJson = DecompressString(blob, compression);
                    }
                    else if (!reader.IsDBNull(2))
                    {
                        fileJson = reader.GetString(2);
                    }
                    else
                    {
                        continue;
                    }

                    var file = JsonConvert.DeserializeObject<UpdateFile>(fileJson);
                    if (file != null)
                    {
                        files.Add(file);
                    }
                }

                return files;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public void AddPackage(IPackage package)
        {
            AddPackages(new[] { package });
        }

        public void AddPackages(IEnumerable<IPackage> packages)
        {
            if (packages == null)
            {
                return;
            }

            CheckIndex();

            StateLock.EnterWriteLock();
            try
            {
                var stagedPackages = new List<StagedPackage>();
                var stagedIdentities = new HashSet<IPackageIdentity>();
                var progressArgs = new PackageStoreEventArgs { Total = 0, Current = 0 };
                PackagesAddProgress?.Invoke(this, progressArgs);

                using var transaction = Connection.BeginTransaction();

                foreach (var package in packages)
                {
                    if (package == null || _IdentityToIndexMap.ContainsKey(package.Id) || stagedIdentities.Contains(package.Id))
                    {
                        continue;
                    }

                    var packageIndex = _NextPackageIndex + stagedPackages.Count;
                    var packageType = GetPackageType(package);
                    var metadataStorage = GetMetadataStorageBytes(package);

                    // Do not store the full per-package file list here. File metadata is deduplicated
                    // by SHA1 in file_locations and associated to packages through package_file_map.
                    InsertPackage(package, packageIndex, packageType, metadataStorage.Bytes, metadataStorage.Compression, null, transaction);
                    InsertFileLocations(package, packageIndex, transaction);

                    stagedPackages.Add(new StagedPackage
                    {
                        Package = package,
                        Identity = package.Id,
                        PackageIndex = packageIndex,
                        PackageType = packageType
                    });
                    stagedIdentities.Add(package.Id);

                    progressArgs.Current++;
                    if (progressArgs.Current % 100 == 0)
                    {
                        PackagesAddProgress?.Invoke(this, progressArgs);
                    }
                }

                transaction.Commit();

                foreach (var stagedPackage in stagedPackages)
                {
                    _IdentityToIndexMap.Add(stagedPackage.Identity, stagedPackage.PackageIndex);
                    _IndexToIdentityMap.Add(stagedPackage.PackageIndex, stagedPackage.Identity);
                    _PackageTypeIndex.Add(stagedPackage.PackageIndex, stagedPackage.PackageType);
                    Indexes.IndexPackage(stagedPackage.Package, stagedPackage.PackageIndex);
                    PendingPackages.Add(stagedPackage.Package);
                }

                _NextPackageIndex += stagedPackages.Count;

                if (stagedPackages.Count > 0)
                {
                    IsDirty = true;
                    IsIndexDirty = true;
                }

                PackagesAddProgress?.Invoke(this, progressArgs);
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        private static (byte[] Bytes, string Compression) GetMetadataStorageBytes(IPackage package)
        {
            if (package is MicrosoftUpdatePackage microsoftUpdatePackage &&
                microsoftUpdatePackage.TryGetCompressedMetadataBytes(out var compressedMetadataBytes))
            {
                return (compressedMetadataBytes, CompressionGZip);
            }

            // Fallback for packages rehydrated from stores or other sources: keep the XML bytes once,
            // without expanding into secondary file payloads. Compression is not used as a substitute
            // for correct filtering/deduplication.
            return (ReadAllBytes(package.GetMetadataStream()), CompressionNone);
        }

        private static byte[] CompressString(string value)
        {
            return value == null ? null : CompressBytes(Encoding.UTF8.GetBytes(value));
        }

        private static string DecompressString(byte[] value, string compression)
        {
            return value == null ? null : Encoding.UTF8.GetString(DecompressBytes(value, compression));
        }

        private static byte[] CompressBytes(byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                return value;
            }

            using var output = new MemoryStream();
            using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, true))
            {
                compressor.Write(value, 0, value.Length);
            }

            return output.ToArray();
        }

        private static byte[] DecompressBytes(byte[] value, string compression)
        {
            if (value == null || value.Length == 0 || string.IsNullOrEmpty(compression) ||
                string.Equals(compression, CompressionNone, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            using var input = new MemoryStream(value, false);
            using var output = new MemoryStream();

            if (string.Equals(compression, CompressionBrotli, StringComparison.OrdinalIgnoreCase))
            {
                using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
                decompressor.CopyTo(output);
            }
            else if (string.Equals(compression, CompressionGZip, StringComparison.OrdinalIgnoreCase))
            {
                using var decompressor = new GZipStream(input, CompressionMode.Decompress);
                decompressor.CopyTo(output);
            }
            else
            {
                throw new InvalidDataException($"Unsupported metadata compression format: {compression}");
            }

            return output.ToArray();
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            if (stream == null)
            {
                throw new InvalidDataException("Package metadata stream is missing");
            }

            using (stream)
            using (var memoryStream = new MemoryStream())
            {
                stream.CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
        }

        private static int GetPackageType(IPackage package)
        {
            if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition))
            {
                return partitionDefinition.Factory.GetPackageType(package);
            }

            throw new NotImplementedException($"The package belongs to a partition that was not registered: {package.Id.Partition}");
        }

        private static string SerializeFiles(IPackage package)
        {
            if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition) &&
                partitionDefinition.HasExternalContentFileMetadata &&
                package.Files != null &&
                package.Files.Any())
            {
                return JsonConvert.SerializeObject(package.Files.OfType<UpdateFile>().ToList());
            }

            return null;
        }

        private void InsertPackage(IPackage package, int packageIndex, int packageType, byte[] metadataBytes, string metadataCompression, byte[] filesBlob, SqliteTransaction transaction)
        {
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO packages(package_index, partition, open_id_hex, identity, package_type, metadata, metadata_compression, files_json, files_blob, files_compression)
VALUES ($packageIndex, $partition, $openIdHex, $identity, $packageType, $metadata, $metadataCompression, NULL, $filesBlob, $filesCompression);";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            command.Parameters.AddWithValue("$partition", package.Id.Partition);
            command.Parameters.AddWithValue("$openIdHex", package.Id.OpenIdHex);
            command.Parameters.AddWithValue("$identity", package.Id.ToString());
            command.Parameters.AddWithValue("$packageType", packageType);
            command.Parameters.AddWithValue("$metadata", metadataBytes);
            command.Parameters.AddWithValue("$metadataCompression", metadataCompression);
            command.Parameters.AddWithValue("$filesBlob", (object)filesBlob ?? DBNull.Value);
            command.Parameters.AddWithValue("$filesCompression", filesBlob == null ? DBNull.Value : (object)CompressionBrotli);
            command.ExecuteNonQuery();
        }

        private void InsertFileLocations(IPackage package, int packageIndex, SqliteTransaction transaction)
        {
            if (package.Files == null)
            {
                return;
            }

            foreach (var file in package.Files.OfType<UpdateFile>())
            {
                var sha1 = file.Digests?
                    .FirstOrDefault(d => string.Equals(d.Algorithm, "SHA1", StringComparison.OrdinalIgnoreCase));

                if (sha1 == null || string.IsNullOrEmpty(sha1.DigestBase64))
                {
                    continue;
                }

                byte[] sha1Bytes;
                try
                {
                    sha1Bytes = Convert.FromBase64String(sha1.DigestBase64);
                }
                catch (FormatException)
                {
                    continue;
                }

                var fileJson = JsonConvert.SerializeObject(file);
                var fileBlob = CompressString(fileJson);
                var preferredUrl = GetPreferredUrl(file, sha1.DigestBase64);

                using (var command = Connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO file_locations(sha1_base64, sha1, sha1_hex, mu_url, file_name, file_json, file_blob, file_compression, package_index)
VALUES ($sha1Base64, $sha1, $sha1Hex, $muUrl, $fileName, $fileJson, $fileBlob, $fileCompression, $packageIndex)
ON CONFLICT(sha1_base64) DO UPDATE SET
    sha1 = excluded.sha1,
    sha1_hex = excluded.sha1_hex,
    mu_url = COALESCE(excluded.mu_url, file_locations.mu_url),
    file_name = COALESCE(excluded.file_name, file_locations.file_name),
    file_json = COALESCE(excluded.file_json, file_locations.file_json),
    file_blob = COALESCE(excluded.file_blob, file_locations.file_blob),
    file_compression = COALESCE(excluded.file_compression, file_locations.file_compression);";
                    command.Parameters.AddWithValue("$sha1Base64", sha1.DigestBase64);
                    command.Parameters.AddWithValue("$sha1", sha1Bytes);
                    command.Parameters.AddWithValue("$sha1Hex", BitConverter.ToString(sha1Bytes).Replace("-", ""));
                    command.Parameters.AddWithValue("$muUrl", (object)preferredUrl ?? DBNull.Value);
                    command.Parameters.AddWithValue("$fileName", (object)file.FileName ?? DBNull.Value);
                    command.Parameters.AddWithValue("$fileJson", _FileLocationFileJsonIsRequired ? (object)fileJson : DBNull.Value);
                    command.Parameters.AddWithValue("$fileBlob", fileBlob);
                    command.Parameters.AddWithValue("$fileCompression", CompressionBrotli);
                    command.Parameters.AddWithValue("$packageIndex", packageIndex);
                    command.ExecuteNonQuery();
                }

                using (var mapCommand = Connection.CreateCommand())
                {
                    mapCommand.Transaction = transaction;
                    mapCommand.CommandText = @"
INSERT OR IGNORE INTO package_file_map(package_index, sha1_base64)
VALUES ($packageIndex, $sha1Base64);";
                    mapCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                    mapCommand.Parameters.AddWithValue("$sha1Base64", sha1.DigestBase64);
                    mapCommand.ExecuteNonQuery();
                }
            }
        }

        private static string GetPreferredUrl(UpdateFile file, string digestBase64)
        {
            var urlsForDigest = file.Urls?
                .Where(u => string.Equals(u.DigestBase64, digestBase64, StringComparison.Ordinal))
                .ToList();

            return urlsForDigest?
                .FirstOrDefault(u => !string.IsNullOrEmpty(u.MuUrl))?
                .MuUrl
                ?? file.Urls?
                    .FirstOrDefault(u => !string.IsNullOrEmpty(u.MuUrl))?
                    .MuUrl
                ?? urlsForDigest?
                    .FirstOrDefault(u => !string.IsNullOrEmpty(u.UssUrl))?
                    .UssUrl
                ?? file.Urls?
                    .FirstOrDefault(u => !string.IsNullOrEmpty(u.UssUrl))?
                    .UssUrl;
        }

        public static void OptimizeExisting(string path, bool replaceDatabaseFile, bool rebuildIndexes, Action<string> log = null)
        {
            if (!Exists(path))
            {
                throw new FileNotFoundException("The SQLite metadata store does not exist", Path.Combine(path, DatabaseFileName));
            }

            using (var store = new SQLitePackageStore(path, FileMode.Open, false))
            {
                store.OptimizeRows(rebuildIndexes, log);
            }

            if (replaceDatabaseFile)
            {
                VacuumIntoAndReplace(path, log);
            }
        }

        private void OptimizeRows(bool rebuildIndexes, Action<string> log)
        {
            StateLock.EnterWriteLock();
            try
            {
                log?.Invoke("Compressing package XML metadata stored in SQLite...");
                var compressedMetadataRows = CompressUncompressedPackageMetadata(log);

                log?.Invoke("Compressing package file metadata stored in SQLite...");
                var compressedPackageFileRows = CompressPackageFiles(log);

                log?.Invoke("Rebuilding compact SHA1 -> Microsoft URL lookup table...");
                var rebuiltFileLocationRows = RebuildFileLocationsTableCompressed(log);

                if (rebuildIndexes)
                {
                    log?.Invoke("Rebuilding indexes without embedding the full file-location payload...");
                    CheckIndex(true);
                    WriteIndexes();
                    IsDirty = false;
                    IsIndexDirty = false;
                }

                WriteProperty("schema_version", SchemaVersion.ToString());
                WriteProperty("metadata_compression", CompressionBrotli);
                WriteProperty("files_compression", CompressionBrotli);
                WriteProperty("file_locations_compression", CompressionBrotli);

                ExecuteNonQuery("PRAGMA optimize;");
                ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");

                log?.Invoke($"SQLite store optimization done. Compressed metadata rows: {compressedMetadataRows}; package file rows: {compressedPackageFileRows}; file-location rows: {rebuiltFileLocationRows}.");
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        private int CompressUncompressedPackageMetadata(Action<string> log)
        {
            var total = 0;

            while (true)
            {
                var rows = new List<(int PackageIndex, byte[] Metadata)>();

                using (var select = Connection.CreateCommand())
                {
                    select.CommandText = @"
SELECT package_index, metadata
FROM packages
WHERE COALESCE(metadata_compression, 'none') = 'none'
LIMIT $limit;";
                    select.Parameters.AddWithValue("$limit", OptimizeBatchSize);

                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add((reader.GetInt32(0), (byte[])reader[1]));
                    }
                }

                if (rows.Count == 0)
                {
                    break;
                }

                using var transaction = Connection.BeginTransaction();
                using var update = Connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = @"
UPDATE packages
SET metadata = $metadata,
    metadata_compression = $metadataCompression
WHERE package_index = $packageIndex;";
                var packageIndexParameter = update.CreateParameter();
                packageIndexParameter.ParameterName = "$packageIndex";
                update.Parameters.Add(packageIndexParameter);

                var metadataParameter = update.CreateParameter();
                metadataParameter.ParameterName = "$metadata";
                update.Parameters.Add(metadataParameter);

                var compressionParameter = update.CreateParameter();
                compressionParameter.ParameterName = "$metadataCompression";
                compressionParameter.Value = CompressionBrotli;
                update.Parameters.Add(compressionParameter);

                foreach (var row in rows)
                {
                    packageIndexParameter.Value = row.PackageIndex;
                    metadataParameter.Value = CompressBytes(row.Metadata);
                    update.ExecuteNonQuery();
                    total++;
                }

                transaction.Commit();
                log?.Invoke($"Compressed package XML metadata rows: {total}");
            }

            return total;
        }

        private int CompressPackageFiles(Action<string> log)
        {
            var total = 0;

            while (true)
            {
                var rows = new List<(int PackageIndex, string FilesJson)>();

                using (var select = Connection.CreateCommand())
                {
                    select.CommandText = @"
SELECT package_index, files_json
FROM packages
WHERE files_json IS NOT NULL
LIMIT $limit;";
                    select.Parameters.AddWithValue("$limit", OptimizeBatchSize);

                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add((reader.GetInt32(0), reader.GetString(1)));
                    }
                }

                if (rows.Count == 0)
                {
                    break;
                }

                using var transaction = Connection.BeginTransaction();
                using var update = Connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = @"
UPDATE packages
SET files_blob = $filesBlob,
    files_compression = $filesCompression,
    files_json = NULL
WHERE package_index = $packageIndex;";
                var packageIndexParameter = update.CreateParameter();
                packageIndexParameter.ParameterName = "$packageIndex";
                update.Parameters.Add(packageIndexParameter);

                var filesBlobParameter = update.CreateParameter();
                filesBlobParameter.ParameterName = "$filesBlob";
                update.Parameters.Add(filesBlobParameter);

                var compressionParameter = update.CreateParameter();
                compressionParameter.ParameterName = "$filesCompression";
                compressionParameter.Value = CompressionBrotli;
                update.Parameters.Add(compressionParameter);

                foreach (var row in rows)
                {
                    packageIndexParameter.Value = row.PackageIndex;
                    filesBlobParameter.Value = CompressString(row.FilesJson);
                    update.ExecuteNonQuery();
                    total++;
                }

                transaction.Commit();
                log?.Invoke($"Compressed package file-metadata rows: {total}");
            }

            return total;
        }

        private int RebuildFileLocationsTableCompressed(Action<string> log)
        {
            ExecuteNonQuery("PRAGMA foreign_keys=OFF;");
            ExecuteNonQuery("DROP TABLE IF EXISTS file_locations_compact;");
            ExecuteNonQuery(@"
CREATE TABLE file_locations_compact (
    sha1_base64 TEXT PRIMARY KEY,
    sha1 BLOB NOT NULL,
    sha1_hex TEXT NOT NULL,
    mu_url TEXT NULL,
    file_name TEXT NULL,
    file_json TEXT NULL,
    file_blob BLOB NULL,
    file_compression TEXT NULL,
    package_index INTEGER NOT NULL,
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
); ");

            var total = 0;
            string lastSha1Base64 = null;

            while (true)
            {
                var rows = new List<FileLocationCompactRow>();

                using (var select = Connection.CreateCommand())
                {
                    select.CommandText = @"
SELECT sha1_base64, sha1, sha1_hex, mu_url, file_name, file_blob, file_compression, file_json, package_index
FROM file_locations
WHERE $lastSha1Base64 IS NULL OR sha1_base64 > $lastSha1Base64
ORDER BY sha1_base64
LIMIT $limit;";
                    select.Parameters.AddWithValue("$lastSha1Base64", (object)lastSha1Base64 ?? DBNull.Value);
                    select.Parameters.AddWithValue("$limit", OptimizeBatchSize);

                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add(new FileLocationCompactRow
                        {
                            Sha1Base64 = reader.GetString(0),
                            Sha1 = (byte[])reader[1],
                            Sha1Hex = reader.GetString(2),
                            MuUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                            FileName = reader.IsDBNull(4) ? null : reader.GetString(4),
                            FileBlob = reader.IsDBNull(5) ? null : (byte[])reader[5],
                            FileCompression = reader.IsDBNull(6) ? null : reader.GetString(6),
                            FileJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                            PackageIndex = reader.GetInt32(8)
                        });
                    }
                }

                if (rows.Count == 0)
                {
                    break;
                }

                using var transaction = Connection.BeginTransaction();
                using var insert = Connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"
INSERT INTO file_locations_compact(sha1_base64, sha1, sha1_hex, mu_url, file_name, file_json, file_blob, file_compression, package_index)
VALUES ($sha1Base64, $sha1, $sha1Hex, $muUrl, $fileName, NULL, $fileBlob, $fileCompression, $packageIndex);";

                var sha1Base64Parameter = insert.CreateParameter();
                sha1Base64Parameter.ParameterName = "$sha1Base64";
                insert.Parameters.Add(sha1Base64Parameter);

                var sha1Parameter = insert.CreateParameter();
                sha1Parameter.ParameterName = "$sha1";
                insert.Parameters.Add(sha1Parameter);

                var sha1HexParameter = insert.CreateParameter();
                sha1HexParameter.ParameterName = "$sha1Hex";
                insert.Parameters.Add(sha1HexParameter);

                var muUrlParameter = insert.CreateParameter();
                muUrlParameter.ParameterName = "$muUrl";
                insert.Parameters.Add(muUrlParameter);

                var fileNameParameter = insert.CreateParameter();
                fileNameParameter.ParameterName = "$fileName";
                insert.Parameters.Add(fileNameParameter);

                var fileBlobParameter = insert.CreateParameter();
                fileBlobParameter.ParameterName = "$fileBlob";
                insert.Parameters.Add(fileBlobParameter);

                var fileCompressionParameter = insert.CreateParameter();
                fileCompressionParameter.ParameterName = "$fileCompression";
                insert.Parameters.Add(fileCompressionParameter);

                var packageIndexParameter = insert.CreateParameter();
                packageIndexParameter.ParameterName = "$packageIndex";
                insert.Parameters.Add(packageIndexParameter);

                foreach (var row in rows)
                {
                    var fileBlob = row.FileBlob;
                    var fileCompression = row.FileCompression;

                    if (fileBlob == null && !string.IsNullOrEmpty(row.FileJson))
                    {
                        fileBlob = CompressString(row.FileJson);
                        fileCompression = CompressionBrotli;
                    }

                    sha1Base64Parameter.Value = row.Sha1Base64;
                    sha1Parameter.Value = row.Sha1;
                    sha1HexParameter.Value = row.Sha1Hex;
                    muUrlParameter.Value = (object)row.MuUrl ?? DBNull.Value;
                    fileNameParameter.Value = (object)row.FileName ?? DBNull.Value;
                    fileBlobParameter.Value = (object)fileBlob ?? DBNull.Value;
                    fileCompressionParameter.Value = (object)fileCompression ?? DBNull.Value;
                    packageIndexParameter.Value = row.PackageIndex;
                    insert.ExecuteNonQuery();

                    lastSha1Base64 = row.Sha1Base64;
                    total++;
                }

                transaction.Commit();
                log?.Invoke($"Compacted file-location rows: {total}");
            }

            ExecuteNonQuery("DROP TABLE file_locations;");
            ExecuteNonQuery("ALTER TABLE file_locations_compact RENAME TO file_locations;");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_file_locations_package ON file_locations(package_index);");
            ExecuteNonQuery("PRAGMA foreign_keys=ON;");
            _FileLocationFileJsonIsRequired = false;

            return total;
        }

        private static void VacuumIntoAndReplace(string path, Action<string> log)
        {
            var databasePath = Path.Combine(path, DatabaseFileName);
            var compactPath = Path.Combine(path, "metadata.compact.sqlite");

            if (File.Exists(compactPath))
            {
                File.Delete(compactPath);
            }

            log?.Invoke("Creating compact SQLite database copy with VACUUM INTO...");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var checkpoint = connection.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    checkpoint.ExecuteNonQuery();
                }

                using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM INTO '" + compactPath.Replace("'", "''") + "';";
                vacuum.ExecuteNonQuery();
            }

            var backupPath = Path.Combine(path, "metadata.sqlite.before-compact-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            File.Move(databasePath, backupPath);

            foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }

            File.Move(compactPath, databasePath);
            log?.Invoke($"Compacted database installed. Previous database kept as: {backupPath}");
        }

        public void Flush()
        {
            StateLock.EnterWriteLock();
            try
            {
                if (IsDirty || IsIndexDirty)
                {
                    WriteIndexes();
                    IsDirty = false;
                    IsIndexDirty = false;
                    PendingPackages.Clear();
                }
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        private void CheckIndex(bool forceReindex = false)
        {
            StateLock.EnterWriteLock();
            try
            {
                if (!_IsReindexingRequired && !forceReindex)
                {
                    return;
                }

                Indexes.ResetIndex();

                var progressEvent = new PackageStoreEventArgs
                {
                    Total = _IdentityToIndexMap.Count,
                    Current = 0
                };

                foreach (var packageIndex in _IndexToIdentityMap.Keys.OrderBy(i => i).ToList())
                {
                    var parsedPackage = CreatePackageFromStoredMetadata(packageIndex);
                    Indexes.IndexPackage(parsedPackage, packageIndex);

                    progressEvent.Current++;
                    if (progressEvent.Current % 100 == 0)
                    {
                        PackageIndexingProgress?.Invoke(this, progressEvent);
                    }
                }

                PackageIndexingProgress?.Invoke(this, progressEvent);
                _IsReindexingRequired = false;
                IsIndexDirty = true;
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public void ReIndex()
        {
            CheckIndex(true);
        }

        public IReadOnlyList<IPackage> GetPendingPackages()
        {
            StateLock.EnterReadLock();
            try
            {
                return PendingPackages.ToList().AsReadOnly();
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public void CopyTo(IMetadataSink destination, CancellationToken cancelToken)
        {
            var packagesIdsToCopy = GetPackageIdentities();

            if (destination is IMetadataStore destinationPackageStore)
            {
                packagesIdsToCopy = packagesIdsToCopy.Except(destinationPackageStore.GetPackageIdentities()).ToList();
            }

            var progressArgs = new PackageStoreEventArgs { Total = packagesIdsToCopy.Count, Current = 0 };
            MetadataCopyProgress?.Invoke(this, progressArgs);

            foreach (var packageId in packagesIdsToCopy)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }

                destination.AddPackage(GetPackage(packageId));
                progressArgs.Current++;
                if (progressArgs.Current % 100 == 0)
                {
                    MetadataCopyProgress?.Invoke(this, progressArgs);
                }
            }

            MetadataCopyProgress?.Invoke(this, progressArgs);
        }

        public void CopyTo(IMetadataSink destination, IMetadataFilter filter, CancellationToken cancelToken)
        {
            var packagesMatchingFilter = filter.Apply(this);
            var packagesIdsToCopy = packagesMatchingFilter.Select(p => p.Id).ToList();

            if (destination is IMetadataStore destinationPackageStore)
            {
                packagesIdsToCopy = packagesIdsToCopy.Except(destinationPackageStore.GetPackageIdentities()).ToList();
            }

            var progressArgs = new PackageStoreEventArgs { Total = packagesIdsToCopy.Count, Current = 0 };
            MetadataCopyProgress?.Invoke(this, progressArgs);

            foreach (var packageId in packagesIdsToCopy)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }

                destination.AddPackage(GetPackage(packageId));
                progressArgs.Current++;
                if (progressArgs.Current % 100 == 0)
                {
                    MetadataCopyProgress?.Invoke(this, progressArgs);
                }
            }

            MetadataCopyProgress?.Invoke(this, progressArgs);
        }

        public bool TrySimpleKeyLookup<T>(IPackageIdentity packageIdentity, string indexName, out T value)
        {
            StateLock.EnterReadLock();
            try
            {
                if (!_IdentityToIndexMap.TryGetValue(packageIdentity, out var packageIndex))
                {
                    throw new KeyNotFoundException();
                }

                return Indexes.TrySimpleKeyLookup(packageIndex, indexName, out value);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public bool TryListKeyLookup<T>(IPackageIdentity packageIdentity, string indexName, out List<T> value)
        {
            StateLock.EnterReadLock();
            try
            {
                if (!_IdentityToIndexMap.TryGetValue(packageIdentity, out var packageIndex))
                {
                    throw new KeyNotFoundException();
                }

                if (string.Equals(indexName, Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.FilesIndexName, StringComparison.Ordinal) &&
                    typeof(T) == typeof(UpdateFile))
                {
                    value = GetFiles<UpdateFile>(packageIdentity).Cast<T>().ToList();
                    return value.Count > 0;
                }

                return Indexes.TryListKeyLookup(packageIndex, indexName, out value);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public bool TryPackageLookupByCustomKey<T>(T key, string indexName, out IPackageIdentity value)
        {
            StateLock.EnterReadLock();
            try
            {
                if (Indexes.TryPackageLookupByCustomKey(key, indexName, out var packageIndex))
                {
                    return _IndexToIdentityMap.TryGetValue(packageIndex, out value);
                }

                value = null;
                return false;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public bool TryPackageListLookupByCustomKey<T>(T key, string indexName, out List<IPackageIdentity> value)
        {
            StateLock.EnterReadLock();
            try
            {
                if (Indexes.TryPackageListLookupByCustomKey(key, indexName, out List<int> packageIndexes))
                {
                    value = packageIndexes
                        .Where(packageIndex => _IndexToIdentityMap.ContainsKey(packageIndex))
                        .Select(packageIndex => _IndexToIdentityMap[packageIndex])
                        .ToList();
                    return true;
                }

                value = null;
                return false;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public List<IndexDefinition> GetAvailableIndexes()
        {
            StateLock.EnterReadLock();
            try
            {
                return Indexes.GetLoadedIndexes();
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        IEnumerator<IPackage> IEnumerable<IPackage>.GetEnumerator()
        {
            return new MetadataEnumerator(this, GetPackageIdentities());
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new MetadataEnumerator(this, GetPackageIdentities());
        }

        public void Dispose()
        {
            StateLock.EnterWriteLock();
            try
            {
                if (IsDisposed)
                {
                    return;
                }

                Flush();

                Indexes?.CloseInput();
                IndexBackingStream?.Dispose();
                Connection?.Dispose();

                _IndexToIdentityMap.Clear();
                _IdentityToIndexMap.Clear();
                _PackageTypeIndex.Clear();
                PendingPackages.Clear();

                IsDisposed = true;
            }
            finally
            {
                StateLock.ExitWriteLock();
                StateLock.Dispose();
            }
        }

        private sealed class FileLocationCompactRow
        {
            public string Sha1Base64 { get; set; }
            public byte[] Sha1 { get; set; }
            public string Sha1Hex { get; set; }
            public string MuUrl { get; set; }
            public string FileName { get; set; }
            public byte[] FileBlob { get; set; }
            public string FileCompression { get; set; }
            public string FileJson { get; set; }
            public int PackageIndex { get; set; }
        }

        private sealed class StagedPackage
        {
            public IPackage Package { get; set; }
            public IPackageIdentity Identity { get; set; }
            public int PackageIndex { get; set; }
            public int PackageType { get; set; }
        }

        private sealed class MetadataEnumerator : IEnumerator<IPackage>
        {
            private readonly SQLitePackageStore Source;
            private readonly IEnumerator<IPackageIdentity> IdentitiesEnumerator;

            public MetadataEnumerator(SQLitePackageStore metadataSource, List<IPackageIdentity> identities)
            {
                Source = metadataSource;
                IdentitiesEnumerator = identities.GetEnumerator();
            }

            public object Current => GetCurrent();

            IPackage IEnumerator<IPackage>.Current => GetCurrent();

            private IPackage GetCurrent()
            {
                return Source.GetPackage(IdentitiesEnumerator.Current);
            }

            public void Dispose()
            {
                IdentitiesEnumerator.Dispose();
            }

            public bool MoveNext()
            {
                return IdentitiesEnumerator.MoveNext();
            }

            public void Reset()
            {
                IdentitiesEnumerator.Reset();
            }
        }
    }
}
