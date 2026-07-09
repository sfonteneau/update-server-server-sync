// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using Microsoft.PackageGraph.Storage.Index;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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

        private const int SchemaVersion = 1;
        private const string IndexBlobKey = "indexes.zip";

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

        private SQLitePackageStore(string path, FileMode mode)
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

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            Connection = new SqliteConnection(connectionString);
            Connection.Open();

            ExecuteNonQuery("PRAGMA journal_mode=WAL;");
            ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
            ExecuteNonQuery("PRAGMA foreign_keys=ON;");

            InitializeSchema();
            LoadPackageMaps();
            LoadIndexes();

            if (_IsReindexingRequired)
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
    files_json TEXT NULL,
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
    file_json TEXT NOT NULL,
    package_index INTEGER NOT NULL,
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_file_locations_package
ON file_locations(package_index);
");

            using var command = Connection.CreateCommand();
            command.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ('schema_version', $schemaVersion)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$schemaVersion", SchemaVersion.ToString());
            command.ExecuteNonQuery();
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
            command.CommandText = "SELECT metadata FROM packages WHERE package_index = $packageIndex;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            var value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value)
            {
                throw new KeyNotFoundException();
            }

            return (byte[])value;
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

        private string ReadFilesJson(int packageIndex)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "SELECT files_json FROM packages WHERE package_index = $packageIndex;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? null : (string)value;
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
                command.CommandText = "SELECT file_json FROM file_locations WHERE sha1_base64 = $sha1;";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$sha1";
                command.Parameters.Add(parameter);

                foreach (var digest in requested)
                {
                    parameter.Value = digest;
                    var value = command.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                    {
                        continue;
                    }

                    var file = JsonConvert.DeserializeObject<UpdateFile>((string)value);
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
                    var metadataBytes = ReadAllBytes(package.GetMetadataStream());
                    var filesJson = SerializeFiles(package);

                    InsertPackage(package, packageIndex, packageType, metadataBytes, filesJson, transaction);
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

        private void InsertPackage(IPackage package, int packageIndex, int packageType, byte[] metadataBytes, string filesJson, SqliteTransaction transaction)
        {
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO packages(package_index, partition, open_id_hex, identity, package_type, metadata, files_json)
VALUES ($packageIndex, $partition, $openIdHex, $identity, $packageType, $metadata, $filesJson);";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            command.Parameters.AddWithValue("$partition", package.Id.Partition);
            command.Parameters.AddWithValue("$openIdHex", package.Id.OpenIdHex);
            command.Parameters.AddWithValue("$identity", package.Id.ToString());
            command.Parameters.AddWithValue("$packageType", packageType);
            command.Parameters.AddWithValue("$metadata", metadataBytes);
            command.Parameters.AddWithValue("$filesJson", (object)filesJson ?? DBNull.Value);
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
                var preferredUrl = GetPreferredUrl(file, sha1.DigestBase64);

                using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO file_locations(sha1_base64, sha1, sha1_hex, mu_url, file_name, file_json, package_index)
VALUES ($sha1Base64, $sha1, $sha1Hex, $muUrl, $fileName, $fileJson, $packageIndex)
ON CONFLICT(sha1_base64) DO UPDATE SET
    sha1 = excluded.sha1,
    sha1_hex = excluded.sha1_hex,
    mu_url = COALESCE(excluded.mu_url, file_locations.mu_url),
    file_name = excluded.file_name,
    file_json = excluded.file_json,
    package_index = excluded.package_index;";
                command.Parameters.AddWithValue("$sha1Base64", sha1.DigestBase64);
                command.Parameters.AddWithValue("$sha1", sha1Bytes);
                command.Parameters.AddWithValue("$sha1Hex", BitConverter.ToString(sha1Bytes).Replace("-", ""));
                command.Parameters.AddWithValue("$muUrl", (object)preferredUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$fileName", (object)file.FileName ?? DBNull.Value);
                command.Parameters.AddWithValue("$fileJson", fileJson);
                command.Parameters.AddWithValue("$packageIndex", packageIndex);
                command.ExecuteNonQuery();
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
