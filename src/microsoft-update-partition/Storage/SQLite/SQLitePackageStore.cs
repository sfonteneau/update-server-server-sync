// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate.Index;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Parsers;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Storage.Index;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.XPath;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// SQLite-backed local metadata store.
    ///
    /// Microsoft Update packages are stored as normalized relational rows in
    /// store/metadata.sqlite. This implementation deliberately does not migrate or open
    /// historical zip-delta stores or older SQLite schemas.
    /// </summary>
    partial class SQLitePackageStore : IMetadataSink, IMetadataStore, IMetadataLookup, IMicrosoftUpdateFileLocationLookup, ISyncAnchorStore, ISyncCheckpointStore
    {
        public const string DatabaseFileName = "metadata.sqlite";

        private const int SchemaVersion = 5;
        private const string RelationalStorageModel = "relational-v1";
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
        private HashSet<int> _RelationalPackageIndexes = new();

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

            if (isNewDatabase)
            {
                InitializeSchema();
                EnsureCompressionSchema();
                InitializeRelationalSchema();
                WriteProperty("storage_model", RelationalStorageModel);
            }
            else
            {
                ValidateExistingStoreFormat();
            }

            LoadPackageMaps();
            Indexes = ZipStreamIndexContainer.Create();
            _IsReindexingRequired = false;
        }

        private void ValidateExistingStoreFormat()
        {
            using var tableCommand = Connection.CreateCommand();
            tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='store_properties';";
            if (Convert.ToInt32((long)tableCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException(
                    "Unsupported metadata.sqlite format. Delete the database and run a fresh fetch.");
            }

            var version = ReadProperty("schema_version");
            var storageModel = ReadProperty("storage_model");
            if (!string.Equals(version, SchemaVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                !string.Equals(storageModel, RelationalStorageModel, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unsupported metadata.sqlite schema (version={version ?? "missing"}, model={storageModel ?? "missing"}). " +
                    "Delete metadata.sqlite and run a fresh fetch; automatic migration is intentionally unsupported.");
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

CREATE TABLE IF NOT EXISTS sync_checkpoints (
    checkpoint_id INTEGER PRIMARY KEY,
    anchor_key TEXT NOT NULL UNIQUE,
    anchor_from TEXT NULL,
    anchor_to TEXT NOT NULL,
    total_items INTEGER NOT NULL DEFAULT 0,
    completed_items INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS sync_checkpoint_items (
    checkpoint_id INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    identity TEXT NOT NULL,
    completed INTEGER NOT NULL DEFAULT 0 CHECK(completed IN (0, 1)),
    attempt_count INTEGER NOT NULL DEFAULT 0,
    last_attempt_at TEXT NULL,
    completed_at TEXT NULL,
    last_error TEXT NULL,
    PRIMARY KEY(checkpoint_id, identity),
    FOREIGN KEY(checkpoint_id) REFERENCES sync_checkpoints(checkpoint_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_sync_checkpoint_items_pending
ON sync_checkpoint_items(checkpoint_id, completed, ordinal);
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
            EnsureColumnExists("sync_checkpoints", "total_items", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumnExists("sync_checkpoints", "completed_items", "INTEGER NOT NULL DEFAULT 0");

            ExecuteNonQuery(@"
UPDATE sync_checkpoints
SET total_items = (
        SELECT COUNT(*)
        FROM sync_checkpoint_items
        WHERE sync_checkpoint_items.checkpoint_id = sync_checkpoints.checkpoint_id
    ),
    completed_items = (
        SELECT COUNT(*)
        FROM sync_checkpoint_items
        WHERE sync_checkpoint_items.checkpoint_id = sync_checkpoints.checkpoint_id
          AND sync_checkpoint_items.completed = 1
    );

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
                _RelationalPackageIndexes = new HashSet<int>();

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
                    _RelationalPackageIndexes.Add(packageIndex);

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
            if (!HasLegacyPackages())
            {
                Indexes = ZipStreamIndexContainer.Create();
                _IsReindexingRequired = false;
                return;
            }

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
                _IsReindexingRequired = LegacyPackageCount() > 0;
            }

            var indexedPackageCountString = ReadProperty("indexed_legacy_package_count")
                ?? ReadProperty("indexed_package_count");
            if (!int.TryParse(indexedPackageCountString, out var indexedPackageCount) ||
                indexedPackageCount != LegacyPackageCount())
            {
                _IsReindexingRequired = LegacyPackageCount() > 0;
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

            WriteProperty("indexed_legacy_package_count", LegacyPackageCount().ToString(CultureInfo.InvariantCulture));

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

        public bool TryGetSyncAnchor(string anchorKey, out string anchor)
        {
            anchor = null;
            if (string.IsNullOrEmpty(anchorKey))
            {
                return false;
            }

            StateLock.EnterReadLock();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = "SELECT value FROM store_properties WHERE key = $key;";
                command.Parameters.AddWithValue("$key", anchorKey);
                var result = command.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                {
                    return false;
                }

                anchor = result.ToString();
                return !string.IsNullOrEmpty(anchor);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public void SetSyncAnchor(string anchorKey, string anchor)
        {
            if (string.IsNullOrEmpty(anchorKey) || string.IsNullOrEmpty(anchor))
            {
                return;
            }

            StateLock.EnterWriteLock();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ($key, $value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                command.Parameters.AddWithValue("$key", anchorKey);
                command.Parameters.AddWithValue("$value", anchor);
                command.ExecuteNonQuery();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public void ClearSyncAnchor(string anchorKey)
        {
            if (string.IsNullOrEmpty(anchorKey))
            {
                return;
            }

            StateLock.EnterWriteLock();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = "DELETE FROM store_properties WHERE key = $key;";
                command.Parameters.AddWithValue("$key", anchorKey);
                command.ExecuteNonQuery();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public bool TryGetSyncCheckpoint(string anchorKey, out SyncCheckpointInfo checkpoint)
        {
            checkpoint = null;
            if (string.IsNullOrWhiteSpace(anchorKey))
            {
                return false;
            }

            StateLock.EnterReadLock();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = @"
SELECT
    anchor_from,
    anchor_to,
    total_items,
    completed_items,
    created_at,
    updated_at
FROM sync_checkpoints
WHERE anchor_key = $anchorKey;";
                command.Parameters.AddWithValue("$anchorKey", anchorKey);

                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                checkpoint = new SyncCheckpointInfo(
                    anchorKey,
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetString(1),
                    checked((int)reader.GetInt64(2)),
                    checked((int)reader.GetInt64(3)),
                    ParseCheckpointTimestamp(reader.GetString(4)),
                    ParseCheckpointTimestamp(reader.GetString(5)));
                return true;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public void CreateSyncCheckpoint(
            string anchorKey,
            string anchorFrom,
            string anchorTo,
            IReadOnlyList<IPackageIdentity> packageIdentities)
        {
            if (string.IsNullOrWhiteSpace(anchorKey))
            {
                throw new ArgumentException("A checkpoint anchor key is required", nameof(anchorKey));
            }

            if (string.IsNullOrWhiteSpace(anchorTo))
            {
                throw new ArgumentException("The upstream checkpoint anchor is required", nameof(anchorTo));
            }

            var requestedIdentities = (packageIdentities ?? Array.Empty<IPackageIdentity>())
                .Where(identity => identity != null)
                .Distinct()
                .ToList();
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            StateLock.EnterWriteLock();
            try
            {
                // Existing package rows are already durable and do not need a
                // temporary checkpoint item. This keeps forced full queries on a
                // populated store from duplicating the whole identity list.
                var identities = requestedIdentities
                    .Where(identity => !_IdentityToIndexMap.ContainsKey(identity))
                    .ToList();

                using var transaction = Connection.BeginTransaction();

                using (var existenceCommand = Connection.CreateCommand())
                {
                    existenceCommand.Transaction = transaction;
                    existenceCommand.CommandText =
                        "SELECT COUNT(*) FROM sync_checkpoints WHERE anchor_key = $anchorKey;";
                    existenceCommand.Parameters.AddWithValue("$anchorKey", anchorKey);
                    if ((long)existenceCommand.ExecuteScalar() != 0)
                    {
                        throw new InvalidOperationException(
                            $"An unfinished synchronization checkpoint already exists for {anchorKey}");
                    }
                }

                using (var checkpointCommand = Connection.CreateCommand())
                {
                    checkpointCommand.Transaction = transaction;
                    checkpointCommand.CommandText = @"
INSERT INTO sync_checkpoints(
    anchor_key,
    anchor_from,
    anchor_to,
    total_items,
    completed_items,
    created_at,
    updated_at)
VALUES ($anchorKey, $anchorFrom, $anchorTo, $totalItems, 0, $createdAt, $updatedAt);";
                    checkpointCommand.Parameters.AddWithValue("$anchorKey", anchorKey);
                    checkpointCommand.Parameters.AddWithValue("$anchorFrom", (object)anchorFrom ?? DBNull.Value);
                    checkpointCommand.Parameters.AddWithValue("$anchorTo", anchorTo);
                    checkpointCommand.Parameters.AddWithValue("$totalItems", identities.Count);
                    checkpointCommand.Parameters.AddWithValue("$createdAt", now);
                    checkpointCommand.Parameters.AddWithValue("$updatedAt", now);
                    checkpointCommand.ExecuteNonQuery();
                }

                long checkpointId;
                using (var idCommand = Connection.CreateCommand())
                {
                    idCommand.Transaction = transaction;
                    idCommand.CommandText =
                        "SELECT checkpoint_id FROM sync_checkpoints WHERE anchor_key = $anchorKey;";
                    idCommand.Parameters.AddWithValue("$anchorKey", anchorKey);
                    checkpointId = (long)idCommand.ExecuteScalar();
                }

                using (var itemCommand = Connection.CreateCommand())
                {
                    itemCommand.Transaction = transaction;
                    itemCommand.CommandText = @"
INSERT INTO sync_checkpoint_items(checkpoint_id, ordinal, identity)
VALUES ($checkpointId, $ordinal, $identity);";
                    var checkpointIdParameter = itemCommand.Parameters.Add("$checkpointId", SqliteType.Integer);
                    var ordinalParameter = itemCommand.Parameters.Add("$ordinal", SqliteType.Integer);
                    var identityParameter = itemCommand.Parameters.Add("$identity", SqliteType.Text);
                    itemCommand.Prepare();

                    checkpointIdParameter.Value = checkpointId;
                    for (var index = 0; index < identities.Count; index++)
                    {
                        ordinalParameter.Value = index;
                        identityParameter.Value = identities[index].ToString();
                        itemCommand.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public IReadOnlyList<IPackageIdentity> GetPendingSyncCheckpointItems(string anchorKey, int maxCount)
        {
            if (string.IsNullOrWhiteSpace(anchorKey) || maxCount <= 0)
            {
                return Array.Empty<IPackageIdentity>();
            }

            StateLock.EnterReadLock();
            try
            {
                var identities = new List<IPackageIdentity>(maxCount);
                using var command = Connection.CreateCommand();
                command.CommandText = @"
SELECT item.identity
FROM sync_checkpoint_items AS item
JOIN sync_checkpoints AS checkpoint
  ON checkpoint.checkpoint_id = item.checkpoint_id
WHERE checkpoint.anchor_key = $anchorKey
  AND item.completed = 0
ORDER BY item.ordinal
LIMIT $maxCount;";
                command.Parameters.AddWithValue("$anchorKey", anchorKey);
                command.Parameters.AddWithValue("$maxCount", maxCount);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    identities.Add(IdentityFromString(reader.GetString(0)));
                }

                return identities;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public void MarkSyncCheckpointItemsAttempted(
            string anchorKey,
            IEnumerable<IPackageIdentity> packageIdentities)
        {
            var identityStrings = NormalizeCheckpointIdentityStrings(packageIdentities);
            if (string.IsNullOrWhiteSpace(anchorKey) || identityStrings.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE sync_checkpoint_items
SET attempt_count = attempt_count + 1,
    last_attempt_at = $now,
    last_error = NULL
WHERE checkpoint_id = (
        SELECT checkpoint_id
        FROM sync_checkpoints
        WHERE anchor_key = $anchorKey
    )
  AND identity = $identity
  AND completed = 0;";
                var anchorKeyParameter = command.Parameters.Add("$anchorKey", SqliteType.Text);
                var identityParameter = command.Parameters.Add("$identity", SqliteType.Text);
                var nowParameter = command.Parameters.Add("$now", SqliteType.Text);
                command.Prepare();

                anchorKeyParameter.Value = anchorKey;
                nowParameter.Value = now;
                foreach (var identity in identityStrings)
                {
                    identityParameter.Value = identity;
                    command.ExecuteNonQuery();
                }

                UpdateCheckpointTimestamp(anchorKey, now, transaction);
                transaction.Commit();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public void MarkSyncCheckpointItemsCompleted(
            string anchorKey,
            IEnumerable<IPackageIdentity> packageIdentities)
        {
            var identityStrings = NormalizeCheckpointIdentityStrings(packageIdentities);
            if (string.IsNullOrWhiteSpace(anchorKey) || identityStrings.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE sync_checkpoint_items
SET completed = 1,
    completed_at = COALESCE(completed_at, $now),
    last_error = NULL
WHERE checkpoint_id = (
        SELECT checkpoint_id
        FROM sync_checkpoints
        WHERE anchor_key = $anchorKey
    )
  AND identity = $identity
  AND completed = 0;";
                var anchorKeyParameter = command.Parameters.Add("$anchorKey", SqliteType.Text);
                var identityParameter = command.Parameters.Add("$identity", SqliteType.Text);
                var nowParameter = command.Parameters.Add("$now", SqliteType.Text);
                command.Prepare();

                anchorKeyParameter.Value = anchorKey;
                nowParameter.Value = now;
                var completedDelta = 0;
                foreach (var identity in identityStrings)
                {
                    identityParameter.Value = identity;
                    completedDelta += command.ExecuteNonQuery();
                }

                UpdateCheckpointProgress(anchorKey, completedDelta, now, transaction);
                transaction.Commit();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public void MarkSyncCheckpointItemsFailed(
            string anchorKey,
            IEnumerable<IPackageIdentity> packageIdentities,
            string error)
        {
            var identityStrings = NormalizeCheckpointIdentityStrings(packageIdentities);
            if (string.IsNullOrWhiteSpace(anchorKey) || identityStrings.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var safeError = string.IsNullOrWhiteSpace(error)
                ? "Metadata retrieval failed"
                : error.Length <= 4000 ? error : error.Substring(0, 4000);

            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE sync_checkpoint_items
SET last_attempt_at = COALESCE(last_attempt_at, $now),
    last_error = $error
WHERE checkpoint_id = (
        SELECT checkpoint_id
        FROM sync_checkpoints
        WHERE anchor_key = $anchorKey
    )
  AND identity = $identity
  AND completed = 0;";
                var anchorKeyParameter = command.Parameters.Add("$anchorKey", SqliteType.Text);
                var identityParameter = command.Parameters.Add("$identity", SqliteType.Text);
                var nowParameter = command.Parameters.Add("$now", SqliteType.Text);
                var errorParameter = command.Parameters.Add("$error", SqliteType.Text);
                command.Prepare();

                anchorKeyParameter.Value = anchorKey;
                nowParameter.Value = now;
                errorParameter.Value = safeError;
                foreach (var identity in identityStrings)
                {
                    identityParameter.Value = identity;
                    command.ExecuteNonQuery();
                }

                UpdateCheckpointTimestamp(anchorKey, now, transaction);
                transaction.Commit();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public void ReconcileSyncCheckpoint(string anchorKey)
        {
            if (string.IsNullOrWhiteSpace(anchorKey))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE sync_checkpoint_items
SET completed = 1,
    completed_at = COALESCE(completed_at, $now),
    last_error = NULL
WHERE checkpoint_id = (
        SELECT checkpoint_id
        FROM sync_checkpoints
        WHERE anchor_key = $anchorKey
    )
  AND completed = 0
  AND EXISTS (
      SELECT 1
      FROM packages
      WHERE packages.identity = sync_checkpoint_items.identity
  );";
                command.Parameters.AddWithValue("$anchorKey", anchorKey);
                command.Parameters.AddWithValue("$now", now);
                var completedDelta = command.ExecuteNonQuery();
                UpdateCheckpointProgress(anchorKey, completedDelta, now, transaction);
                transaction.Commit();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public void CompleteSyncCheckpoint(string anchorKey)
        {
            if (string.IsNullOrWhiteSpace(anchorKey))
            {
                throw new ArgumentException("A checkpoint anchor key is required", nameof(anchorKey));
            }

            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                string anchorTo;

                using (var anchorCommand = Connection.CreateCommand())
                {
                    anchorCommand.Transaction = transaction;
                    anchorCommand.CommandText = @"
SELECT anchor_to, total_items, completed_items
FROM sync_checkpoints
WHERE anchor_key = $anchorKey;";
                    anchorCommand.Parameters.AddWithValue("$anchorKey", anchorKey);
                    using var reader = anchorCommand.ExecuteReader();
                    if (!reader.Read())
                    {
                        throw new InvalidOperationException(
                            $"No synchronization checkpoint exists for {anchorKey}");
                    }

                    anchorTo = reader.GetString(0);
                    var totalItems = reader.GetInt64(1);
                    var completedItems = reader.GetInt64(2);
                    if (completedItems != totalItems)
                    {
                        throw new InvalidOperationException(
                            $"Cannot promote the synchronization anchor while " +
                            $"{totalItems - completedItems} checkpoint item(s) are pending");
                    }
                }

                // Verify the counter once at completion. Normal progress reads are
                // O(1); this final indexed count protects against manual database edits.
                using (var pendingCommand = Connection.CreateCommand())
                {
                    pendingCommand.Transaction = transaction;
                    pendingCommand.CommandText = @"
SELECT COUNT(*)
FROM sync_checkpoint_items AS item
JOIN sync_checkpoints AS checkpoint
  ON checkpoint.checkpoint_id = item.checkpoint_id
WHERE checkpoint.anchor_key = $anchorKey
  AND item.completed = 0;";
                    pendingCommand.Parameters.AddWithValue("$anchorKey", anchorKey);
                    var pendingCount = (long)pendingCommand.ExecuteScalar();
                    if (pendingCount != 0)
                    {
                        throw new InvalidOperationException(
                            $"Cannot promote the synchronization anchor while " +
                            $"{pendingCount} checkpoint item(s) are pending");
                    }
                }

                using (var propertyCommand = Connection.CreateCommand())
                {
                    propertyCommand.Transaction = transaction;
                    propertyCommand.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ($anchorKey, $anchorTo)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                    propertyCommand.Parameters.AddWithValue("$anchorKey", anchorKey);
                    propertyCommand.Parameters.AddWithValue("$anchorTo", anchorTo);
                    propertyCommand.ExecuteNonQuery();
                }

                using (var deleteCommand = Connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText =
                        "DELETE FROM sync_checkpoints WHERE anchor_key = $anchorKey;";
                    deleteCommand.Parameters.AddWithValue("$anchorKey", anchorKey);
                    deleteCommand.ExecuteNonQuery();
                }

                transaction.Commit();
                TryReclaimCheckpointStorage();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public void ClearSyncCheckpoint(string anchorKey)
        {
            if (string.IsNullOrWhiteSpace(anchorKey))
            {
                return;
            }

            StateLock.EnterWriteLock();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = "DELETE FROM sync_checkpoints WHERE anchor_key = $anchorKey;";
                command.Parameters.AddWithValue("$anchorKey", anchorKey);
                command.ExecuteNonQuery();
                TryReclaimCheckpointStorage();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        private void TryReclaimCheckpointStorage()
        {
            try
            {
                // New SQLite stores use auto_vacuum=INCREMENTAL. Reclaim the
                // temporary checkpoint pages after success/reset so a large initial
                // revision list does not become permanent database bloat. On older
                // stores without incremental auto-vacuum this PRAGMA is a no-op.
                ExecuteNonQuery("PRAGMA incremental_vacuum;");
            }
            catch
            {
                // Space reclamation is best-effort and must never invalidate a
                // successfully promoted anchor or a user-requested checkpoint reset.
            }
        }

        private static List<string> NormalizeCheckpointIdentityStrings(
            IEnumerable<IPackageIdentity> packageIdentities)
        {
            return (packageIdentities ?? Array.Empty<IPackageIdentity>())
                .Where(identity => identity != null)
                .Select(identity => identity.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static DateTimeOffset ParseCheckpointTimestamp(string value)
        {
            if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
            {
                return timestamp;
            }

            return DateTimeOffset.MinValue;
        }

        private void UpdateCheckpointProgress(
            string anchorKey,
            int completedDelta,
            string timestamp,
            SqliteTransaction transaction)
        {
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE sync_checkpoints
SET completed_items = MIN(total_items, completed_items + $completedDelta),
    updated_at = $updatedAt
WHERE anchor_key = $anchorKey;";
            command.Parameters.AddWithValue("$anchorKey", anchorKey);
            command.Parameters.AddWithValue("$completedDelta", Math.Max(0, completedDelta));
            command.Parameters.AddWithValue("$updatedAt", timestamp);
            command.ExecuteNonQuery();
        }

        private void UpdateCheckpointTimestamp(
            string anchorKey,
            string timestamp,
            SqliteTransaction transaction)
        {
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE sync_checkpoints
SET updated_at = $updatedAt
WHERE anchor_key = $anchorKey;";
            command.Parameters.AddWithValue("$anchorKey", anchorKey);
            command.Parameters.AddWithValue("$updatedAt", timestamp);
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
            return BuildRelationalMetadataBytes(packageIndex);
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

                if (typeof(T) != typeof(UpdateFile))
                {
                    return new List<T>();
                }

                return ReadRelationalFiles(packageIndex).Cast<T>().ToList();
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        private List<UpdateFile> ReadFilesFromMetadata(int packageIndex)
        {
            byte[] metadataBytes;
            try
            {
                metadataBytes = ReadMetadataBytes(packageIndex);
            }
            catch
            {
                return new List<UpdateFile>();
            }

            using var metadataStream = new MemoryStream(metadataBytes, false);
            XPathDocument document = new(metadataStream);
            XPathNavigator navigator = document.CreateNavigator();

            XmlNamespaceManager manager = new(navigator.NameTable);
            manager.AddNamespace("upd", "http://schemas.microsoft.com/msus/2002/12/Update");

            var files = UpdateFileParser.ParseFiles(navigator, manager);
            if (files.Count == 0)
            {
                return files;
            }

            var urlMap = ReadPackageFileUrls(packageIndex);
            foreach (var file in files)
            {
                file.Urls = new List<UpdateFileUrl>();
                foreach (var digest in file.Digests ?? Enumerable.Empty<ContentFileDigest>())
                {
                    if (urlMap.TryGetValue(digest.DigestBase64, out var url))
                    {
                        file.Urls.Add(url);
                    }
                }
            }

            return files;
        }

        private Dictionary<string, UpdateFileUrl> ReadPackageFileUrls(int packageIndex)
        {
            var urls = new Dictionary<string, UpdateFileUrl>(StringComparer.Ordinal);
            using var command = Connection.CreateCommand();
            command.CommandText = @"
SELECT fl.sha1_base64, fl.mu_url, fl.uss_url
FROM package_file_map pfm
JOIN file_locations fl ON fl.sha1_base64 = pfm.sha1_base64
WHERE pfm.package_index = $packageIndex
  AND (fl.mu_url IS NOT NULL OR fl.uss_url IS NOT NULL);";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var digestBase64 = reader.GetString(0);
                var muUrl = reader.IsDBNull(1) ? null : reader.GetString(1);
                var ussUrl = reader.IsDBNull(2) ? null : reader.GetString(2);
                urls[digestBase64] = new UpdateFileUrl(digestBase64, muUrl, ussUrl);
            }

            return urls;
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
                foreach (var digest in requested)
                {
                    var file = ReadRelationalFileBySha1(digest);
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

                    if (package is not MicrosoftUpdatePackage microsoftUpdatePackage)
                    {
                        throw new NotSupportedException(
                            "The clean relational local store only accepts Microsoft Update packages.");
                    }

                    var packageIndex = _NextPackageIndex + stagedPackages.Count;
                    var packageType = GetPackageType(package);
                    const bool isRelational = true;
                    var relationalRecord = RelationalMetadataExtractor.Extract(microsoftUpdatePackage);
                    InsertRelationalPackage(
                        package,
                        packageIndex,
                        packageType,
                        relationalRecord,
                        transaction);

                    stagedPackages.Add(new StagedPackage
                    {
                        Package = package,
                        Identity = package.Id,
                        PackageIndex = packageIndex,
                        PackageType = packageType,
                        IsRelational = isRelational
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
                    if (stagedPackage.IsRelational)
                    {
                        _RelationalPackageIndexes.Add(stagedPackage.PackageIndex);
                    }
                    else
                    {
                        Indexes.IndexPackage(stagedPackage.Package, stagedPackage.PackageIndex);
                        IsIndexDirty = true;
                    }

                    PendingPackages.Add(stagedPackage.Package);
                }

                _NextPackageIndex += stagedPackages.Count;

                if (stagedPackages.Count > 0)
                {
                    IsDirty = true;
                    UpdateStorageModelProperty();
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

                string fileJson = null;
                byte[] fileBlob = null;

                // New stores do not duplicate full UpdateFile JSON in file_locations. File
                // metadata is parsed from the package XML when needed; this table only keeps
                // the SHA1 -> URL lookup required for direct Microsoft downloads.
                if (_FileLocationFileJsonIsRequired)
                {
                    fileJson = JsonConvert.SerializeObject(file);
                }

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
                    command.Parameters.AddWithValue("$fileBlob", (object)fileBlob ?? DBNull.Value);
                    command.Parameters.AddWithValue("$fileCompression", fileBlob == null ? DBNull.Value : (object)CompressionBrotli);
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

            using (var store = new SQLitePackageStore(path, FileMode.Open))
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
                log?.Invoke("Optimizing SQLite metadata rows...");
                var compressedMetadataRows = CompressUncompressedPackageMetadata(log);
                var prunedPackageFileRows = PrunePackageFiles(log);

                using (var countCommand = Connection.CreateCommand())
                {
                    countCommand.CommandText = @"
SELECT COUNT(*)
FROM file_locations
WHERE file_json IS NOT NULL
   OR file_blob IS NOT NULL
   OR file_compression IS NOT NULL;";
                    var filePayloadRows = Convert.ToInt32((long)countCommand.ExecuteScalar());
                    ExecuteNonQuery(@"
UPDATE file_locations
SET file_json = NULL,
    file_blob = NULL,
    file_compression = NULL
WHERE file_json IS NOT NULL
   OR file_blob IS NOT NULL
   OR file_compression IS NOT NULL;");
                    log?.Invoke($"Pruned duplicated file-location payload rows: {filePayloadRows}");
                }

                if (rebuildIndexes && HasLegacyPackages())
                {
                    log?.Invoke("Rebuilding indexes for the remaining legacy XML packages...");
                    CheckIndex(true);
                    WriteIndexes();
                    IsIndexDirty = false;
                }
                else if (!HasLegacyPackages())
                {
                    ExecuteNonQuery("DELETE FROM store_blobs WHERE key = 'indexes.zip';");
                    ExecuteNonQuery("DELETE FROM store_properties WHERE key IN ('indexed_package_count', 'indexed_legacy_package_count');");
                }

                IsDirty = false;
                WriteProperty("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
                UpdateStorageModelProperty();
                WriteProperty("files_storage", "normalized-file-location-map");
                WriteProperty("file_locations_storage", "sha1-digests-url-relational");

                ExecuteNonQuery("PRAGMA optimize;");
                ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");

                log?.Invoke($"SQLite store optimization done. Compressed legacy metadata rows: {compressedMetadataRows}; pruned package file rows: {prunedPackageFileRows}.");
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

        private int PrunePackageFiles(Action<string> log)
        {
            using var countCommand = Connection.CreateCommand();
            countCommand.CommandText = @"
SELECT COUNT(*)
FROM packages
WHERE files_json IS NOT NULL
   OR files_blob IS NOT NULL
   OR files_compression IS NOT NULL;";
            var total = Convert.ToInt32((long)countCommand.ExecuteScalar());

            using var transaction = Connection.BeginTransaction();
            using var update = Connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"
UPDATE packages
SET files_json = NULL,
    files_blob = NULL,
    files_compression = NULL
WHERE files_json IS NOT NULL
   OR files_blob IS NOT NULL
   OR files_compression IS NOT NULL;";
            update.ExecuteNonQuery();
            transaction.Commit();

            log?.Invoke($"Pruned duplicated package file-metadata rows: {total}");
            return total;
        }

        private int RebuildFileLocationsTableCompact(Action<string> log)
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
VALUES ($sha1Base64, $sha1, $sha1Hex, $muUrl, $fileName, NULL, NULL, NULL, $packageIndex);";

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
                    sha1Base64Parameter.Value = row.Sha1Base64;
                    sha1Parameter.Value = row.Sha1;
                    sha1HexParameter.Value = row.Sha1Hex;
                    muUrlParameter.Value = (object)row.MuUrl ?? DBNull.Value;
                    fileNameParameter.Value = (object)row.FileName ?? DBNull.Value;
                    fileBlobParameter.Value = DBNull.Value;
                    fileCompressionParameter.Value = DBNull.Value;
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
                IsDirty = false;
                IsIndexDirty = false;
                PendingPackages.Clear();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        private void CheckIndex(bool forceReindex = false)
        {
            _IsReindexingRequired = false;
            IsIndexDirty = false;
        }

        public void ReIndex()
        {
            // Relational indexes are maintained transactionally by SQLite.
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

                return TryRelationalSimpleKeyLookup(packageIndex, indexName, out value);
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

                return TryRelationalListKeyLookup(packageIndex, indexName, out value);
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
                if (TryRelationalPackageListLookupByCustomKey(key, indexName, out var relationalValues) &&
                    relationalValues.Count > 0)
                {
                    value = relationalValues[0];
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

        public bool TryPackageListLookupByCustomKey<T>(T key, string indexName, out List<IPackageIdentity> value)
        {
            StateLock.EnterReadLock();
            try
            {
                var results = new List<IPackageIdentity>();
                if (TryRelationalPackageListLookupByCustomKey(key, indexName, out var relationalValues) &&
                    relationalValues != null)
                {
                    results.AddRange(relationalValues);
                }

                value = results.Distinct().ToList();
                return value.Count > 0;
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
                return GetRelationalIndexDefinitions();
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
                _RelationalPackageIndexes.Clear();
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
            public bool IsRelational { get; set; }
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
