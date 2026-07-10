// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate.Index;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Storage.Index;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.PackageGraph.MicrosoftUpdate;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Drivers;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using System.Security.Cryptography;
using System.Xml.Linq;

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
        private bool IsDisposed;
        private bool _IsReindexingRequired;


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
                InitializeRelationalSchema();
                WriteProperty("storage_model", RelationalStorageModel);
            }
            else
            {
                ValidateExistingStoreFormat();
            }

            LoadPackageMaps();
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
                    _RelationalPackageIndexes.Add(stagedPackage.PackageIndex);

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
private static int GetPackageType(IPackage package)
        {
            if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition))
            {
                return partitionDefinition.Factory.GetPackageType(package);
            }

            throw new NotImplementedException($"The package belongs to a partition that was not registered: {package.Id.Partition}");
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
                log?.Invoke("Optimizing relational SQLite metadata store...");
                WriteProperty("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
                WriteProperty("storage_model", RelationalStorageModel);
                WriteProperty("files_storage", "normalized-file-location-map");
                WriteProperty("file_locations_storage", "sha1-digests-url-relational");
                ExecuteNonQuery("PRAGMA optimize;");
                ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
                log?.Invoke("Relational SQLite store optimization completed.");
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
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
                PendingPackages.Clear();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
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

        private static readonly XNamespace UpdateNamespace = "http://schemas.microsoft.com/msus/2002/12/Update";
        private static readonly XNamespace DriverNamespace = "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/WindowsDriver";

        private void InitializeRelationalSchema()
        {
            EnsureColumnExists("packages", "storage_model", "TEXT NOT NULL DEFAULT 'relational-v1'");
            EnsureColumnExists("packages", "update_id", "TEXT NULL");
            EnsureColumnExists("packages", "revision_number", "INTEGER NULL");

            EnsureColumnExists("file_locations", "size", "INTEGER NULL");
            EnsureColumnExists("file_locations", "modified", "TEXT NULL");
            EnsureColumnExists("file_locations", "patching_type", "TEXT NULL");
            EnsureColumnExists("file_locations", "uss_url", "TEXT NULL");
            EnsureColumnExists("package_file_map", "ordinal", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumnExists("package_file_map", "file_name", "TEXT NULL");
            EnsureColumnExists("package_file_map", "size", "INTEGER NULL");
            EnsureColumnExists("package_file_map", "modified", "TEXT NULL");
            EnsureColumnExists("package_file_map", "patching_type", "TEXT NULL");

            ExecuteNonQuery(@"
CREATE INDEX IF NOT EXISTS idx_packages_storage_model
ON packages(storage_model, package_index);

CREATE INDEX IF NOT EXISTS idx_packages_update_identity
ON packages(update_id, revision_number);

CREATE INDEX IF NOT EXISTS idx_package_file_map_order
ON package_file_map(package_index, ordinal);

CREATE TABLE IF NOT EXISTS package_property_attributes (
    package_index INTEGER NOT NULL,
    name TEXT NOT NULL,
    value TEXT NOT NULL,
    PRIMARY KEY(package_index, name),
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS package_property_elements (
    package_index INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    value TEXT NULL,
    xml TEXT NULL,
    PRIMARY KEY(package_index, ordinal),
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_package_property_elements_name
ON package_property_elements(package_index, name);

CREATE TABLE IF NOT EXISTS package_localized_elements (
    package_index INTEGER NOT NULL,
    language TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    value TEXT NULL,
    xml TEXT NULL,
    PRIMARY KEY(package_index, language, ordinal),
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_package_localized_lookup
ON package_localized_elements(package_index, language, name);

CREATE TABLE IF NOT EXISTS xml_fragments (
    fragment_hash TEXT PRIMARY KEY,
    xml TEXT NOT NULL
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS package_fragments (
    package_index INTEGER PRIMARY KEY,
    applicability_hash TEXT NULL,
    handler_hash TEXT NULL,
    applicability_template_xml TEXT NULL,
    handler_specific_xml TEXT NULL,
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE,
    FOREIGN KEY(applicability_hash) REFERENCES xml_fragments(fragment_hash),
    FOREIGN KEY(handler_hash) REFERENCES xml_fragments(fragment_hash)
);

CREATE TABLE IF NOT EXISTS package_relationship_groups (
    package_index INTEGER NOT NULL,
    relationship_type TEXT NOT NULL,
    group_ordinal INTEGER NOT NULL,
    group_kind TEXT NOT NULL,
    is_category INTEGER NOT NULL DEFAULT 0 CHECK(is_category IN (0, 1)),
    PRIMARY KEY(package_index, relationship_type, group_ordinal),
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS package_relationship_items (
    package_index INTEGER NOT NULL,
    relationship_type TEXT NOT NULL,
    group_ordinal INTEGER NOT NULL,
    item_ordinal INTEGER NOT NULL,
    update_id TEXT NOT NULL,
    revision_number INTEGER NULL,
    PRIMARY KEY(package_index, relationship_type, group_ordinal, item_ordinal),
    FOREIGN KEY(package_index, relationship_type, group_ordinal)
        REFERENCES package_relationship_groups(package_index, relationship_type, group_ordinal)
        ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_package_relationship_target
ON package_relationship_items(relationship_type, update_id, revision_number, package_index);

CREATE TABLE IF NOT EXISTS package_relationship_extra_elements (
    package_index INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    xml TEXT NOT NULL,
    PRIMARY KEY(package_index, ordinal),
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS driver_releases (
    driver_release_id INTEGER PRIMARY KEY,
    fingerprint TEXT NOT NULL UNIQUE,
    whql_driver_id TEXT NULL,
    manufacturer TEXT NULL,
    company TEXT NULL,
    provider TEXT NULL,
    driver_date TEXT NOT NULL,
    driver_version TEXT NOT NULL,
    driver_class TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_driver_releases_whql
ON driver_releases(whql_driver_id, driver_release_id);

CREATE TABLE IF NOT EXISTS driver_metadata (
    driver_metadata_id INTEGER PRIMARY KEY,
    fingerprint TEXT NOT NULL UNIQUE,
    driver_release_id INTEGER NOT NULL,
    hardware_id TEXT NULL,
    FOREIGN KEY(driver_release_id) REFERENCES driver_releases(driver_release_id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS idx_driver_metadata_hardware_id
ON driver_metadata(hardware_id, driver_metadata_id);

CREATE INDEX IF NOT EXISTS idx_driver_metadata_release
ON driver_metadata(driver_release_id, driver_metadata_id);

CREATE TABLE IF NOT EXISTS package_driver_metadata (
    package_index INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    driver_metadata_id INTEGER NOT NULL,
    PRIMARY KEY(package_index, ordinal),
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE,
    FOREIGN KEY(driver_metadata_id) REFERENCES driver_metadata(driver_metadata_id) ON DELETE RESTRICT
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_package_driver_metadata_record
ON package_driver_metadata(driver_metadata_id, package_index);

CREATE TABLE IF NOT EXISTS driver_feature_scores (
    driver_metadata_id INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    operating_system TEXT NULL,
    score INTEGER NOT NULL,
    PRIMARY KEY(driver_metadata_id, ordinal),
    FOREIGN KEY(driver_metadata_id) REFERENCES driver_metadata(driver_metadata_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS driver_computer_hardware_ids (
    driver_metadata_id INTEGER NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN ('distribution', 'target')),
    ordinal INTEGER NOT NULL,
    hardware_id TEXT NOT NULL,
    PRIMARY KEY(driver_metadata_id, kind, ordinal),
    FOREIGN KEY(driver_metadata_id) REFERENCES driver_metadata(driver_metadata_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_driver_computer_hardware_id
ON driver_computer_hardware_ids(hardware_id, driver_metadata_id);

CREATE TABLE IF NOT EXISTS file_digests (
    sha1_base64 TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    algorithm TEXT NOT NULL,
    digest_base64 TEXT NOT NULL,
    PRIMARY KEY(sha1_base64, algorithm, digest_base64),
    FOREIGN KEY(sha1_base64) REFERENCES file_locations(sha1_base64) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_file_digests_value
ON file_digests(algorithm, digest_base64);
");

            // CREATE TABLE IF NOT EXISTS does not add columns to a database left by an
            // interrupted earlier build of this patch. Add the content-address columns before
            // creating their indexes; raw XML columns remain a read-only compatibility fallback.
            EnsureColumnExists("package_fragments", "applicability_hash", "TEXT NULL");
            EnsureColumnExists("package_fragments", "handler_hash", "TEXT NULL");
            EnsureColumnExists("package_fragments", "applicability_template_xml", "TEXT NULL");
            EnsureColumnExists("package_fragments", "handler_specific_xml", "TEXT NULL");
            ExecuteNonQuery(@"
CREATE INDEX IF NOT EXISTS idx_package_fragments_applicability
ON package_fragments(applicability_hash);

CREATE INDEX IF NOT EXISTS idx_package_fragments_handler
ON package_fragments(handler_hash);
");

        }

        private void UpdateStorageModelProperty()
        {
            WriteProperty("storage_model", RelationalStorageModel);
        }
        private static byte[] RelationalMetadataSentinel => Array.Empty<byte>();

        private void InsertRelationalPackage(
            IPackage package,
            int packageIndex,
            int packageType,
            RelationalPackageRecord record,
            SqliteTransaction transaction)
        {
            using (var command = Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO packages(
    package_index,
    partition,
    open_id_hex,
    identity,
    package_type,
    metadata,
    metadata_compression,
    files_json,
    files_blob,
    files_compression,
    storage_model,
    update_id,
    revision_number)
VALUES (
    $packageIndex,
    $partition,
    $openIdHex,
    $identity,
    $packageType,
    $metadata,
    $metadataCompression,
    NULL,
    NULL,
    NULL,
    $storageModel,
    $updateId,
    $revisionNumber);";
                command.Parameters.AddWithValue("$packageIndex", packageIndex);
                command.Parameters.AddWithValue("$partition", package.Id.Partition);
                command.Parameters.AddWithValue("$openIdHex", package.Id.OpenIdHex);
                command.Parameters.AddWithValue("$identity", package.Id.ToString());
                command.Parameters.AddWithValue("$packageType", packageType);
                command.Parameters.AddWithValue("$metadata", RelationalMetadataSentinel);
                command.Parameters.AddWithValue("$metadataCompression", RelationalStorageModel);
                command.Parameters.AddWithValue("$storageModel", RelationalStorageModel);
                command.Parameters.AddWithValue("$updateId", record.UpdateId.ToString("D"));
                command.Parameters.AddWithValue("$revisionNumber", record.Revision);
                command.ExecuteNonQuery();
            }

            InsertRelationalRecord(packageIndex, record, transaction);
        }

        private void InsertRelationalRecord(
            int packageIndex,
            RelationalPackageRecord record,
            SqliteTransaction transaction)
        {
            InsertPropertyAttributes(packageIndex, record.PropertyAttributes, transaction);
            InsertPropertyElements(packageIndex, record.PropertyElements, transaction);
            InsertLocalizedElements(packageIndex, record.LocalizedElements, transaction);
            InsertFragments(packageIndex, record, transaction);
            InsertRelationships(packageIndex, record, transaction);
            InsertDriverMetadata(packageIndex, record.DriverMetadata, transaction);
            InsertRelationalFiles(packageIndex, record.Files, transaction);
        }

        private void InsertPropertyAttributes(
            int packageIndex,
            IEnumerable<RelationalNameValue> attributes,
            SqliteTransaction transaction)
        {
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO package_property_attributes(package_index, name, value)
VALUES ($packageIndex, $name, $value);";
            var packageParameter = command.Parameters.Add("$packageIndex", SqliteType.Integer);
            var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
            var valueParameter = command.Parameters.Add("$value", SqliteType.Text);

            foreach (var attribute in attributes)
            {
                packageParameter.Value = packageIndex;
                nameParameter.Value = attribute.Name;
                valueParameter.Value = attribute.Value ?? string.Empty;
                command.ExecuteNonQuery();
            }
        }

        private void InsertPropertyElements(
            int packageIndex,
            IEnumerable<RelationalElementValue> elements,
            SqliteTransaction transaction)
        {
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO package_property_elements(package_index, ordinal, name, value, xml)
VALUES ($packageIndex, $ordinal, $name, $value, $xml);";
            var packageParameter = command.Parameters.Add("$packageIndex", SqliteType.Integer);
            var ordinalParameter = command.Parameters.Add("$ordinal", SqliteType.Integer);
            var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
            var valueParameter = command.Parameters.Add("$value", SqliteType.Text);
            var xmlParameter = command.Parameters.Add("$xml", SqliteType.Text);

            foreach (var element in elements)
            {
                packageParameter.Value = packageIndex;
                ordinalParameter.Value = element.Ordinal;
                nameParameter.Value = element.Name;
                valueParameter.Value = (object)element.Value ?? DBNull.Value;
                xmlParameter.Value = (object)element.Xml ?? DBNull.Value;
                command.ExecuteNonQuery();
            }
        }

        private void InsertLocalizedElements(
            int packageIndex,
            IEnumerable<RelationalLocalizedElement> elements,
            SqliteTransaction transaction)
        {
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO package_localized_elements(package_index, language, ordinal, name, value, xml)
VALUES ($packageIndex, $language, $ordinal, $name, $value, $xml);";
            var packageParameter = command.Parameters.Add("$packageIndex", SqliteType.Integer);
            var languageParameter = command.Parameters.Add("$language", SqliteType.Text);
            var ordinalParameter = command.Parameters.Add("$ordinal", SqliteType.Integer);
            var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
            var valueParameter = command.Parameters.Add("$value", SqliteType.Text);
            var xmlParameter = command.Parameters.Add("$xml", SqliteType.Text);

            foreach (var element in elements)
            {
                packageParameter.Value = packageIndex;
                languageParameter.Value = element.Language;
                ordinalParameter.Value = element.Ordinal;
                nameParameter.Value = element.Name;
                valueParameter.Value = (object)element.Value ?? DBNull.Value;
                xmlParameter.Value = (object)element.Xml ?? DBNull.Value;
                command.ExecuteNonQuery();
            }
        }

        private void InsertFragments(
            int packageIndex,
            RelationalPackageRecord record,
            SqliteTransaction transaction)
        {
            var applicabilityHash = StoreXmlFragment(record.ApplicabilityTemplateXml, transaction);
            var handlerHash = StoreXmlFragment(record.HandlerSpecificXml, transaction);
            if (applicabilityHash == null && handlerHash == null)
            {
                return;
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO package_fragments(
    package_index,
    applicability_hash,
    handler_hash,
    applicability_template_xml,
    handler_specific_xml)
VALUES ($packageIndex, $applicabilityHash, $handlerHash, NULL, NULL);";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            command.Parameters.AddWithValue("$applicabilityHash", (object)applicabilityHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$handlerHash", (object)handlerHash ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        private string StoreXmlFragment(string xml, SqliteTransaction transaction)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return null;
            }

            string fragmentHash;
            using (var sha256 = SHA256.Create())
            {
                fragmentHash = Convert.ToHexString(
                    sha256.ComputeHash(Encoding.UTF8.GetBytes(xml)))
                    .ToLowerInvariant();
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO xml_fragments(fragment_hash, xml)
VALUES ($fragmentHash, $xml)
ON CONFLICT(fragment_hash) DO NOTHING;";
            command.Parameters.AddWithValue("$fragmentHash", fragmentHash);
            command.Parameters.AddWithValue("$xml", xml);
            command.ExecuteNonQuery();
            return fragmentHash;
        }

        private void InsertRelationships(
            int packageIndex,
            RelationalPackageRecord record,
            SqliteTransaction transaction)
        {
            using (var groupCommand = Connection.CreateCommand())
            {
                groupCommand.Transaction = transaction;
                groupCommand.CommandText = @"
INSERT INTO package_relationship_groups(
    package_index,
    relationship_type,
    group_ordinal,
    group_kind,
    is_category)
VALUES ($packageIndex, $relationshipType, $groupOrdinal, $groupKind, $isCategory);";
                var packageParameter = groupCommand.Parameters.Add("$packageIndex", SqliteType.Integer);
                var relationshipTypeParameter = groupCommand.Parameters.Add("$relationshipType", SqliteType.Text);
                var groupOrdinalParameter = groupCommand.Parameters.Add("$groupOrdinal", SqliteType.Integer);
                var groupKindParameter = groupCommand.Parameters.Add("$groupKind", SqliteType.Text);
                var isCategoryParameter = groupCommand.Parameters.Add("$isCategory", SqliteType.Integer);

                foreach (var group in record.RelationshipGroups)
                {
                    packageParameter.Value = packageIndex;
                    relationshipTypeParameter.Value = group.RelationshipType;
                    groupOrdinalParameter.Value = group.GroupOrdinal;
                    groupKindParameter.Value = group.GroupKind;
                    isCategoryParameter.Value = group.IsCategory ? 1 : 0;
                    groupCommand.ExecuteNonQuery();
                }
            }

            using (var itemCommand = Connection.CreateCommand())
            {
                itemCommand.Transaction = transaction;
                itemCommand.CommandText = @"
INSERT INTO package_relationship_items(
    package_index,
    relationship_type,
    group_ordinal,
    item_ordinal,
    update_id,
    revision_number)
VALUES ($packageIndex, $relationshipType, $groupOrdinal, $itemOrdinal, $updateId, $revisionNumber);";
                var packageParameter = itemCommand.Parameters.Add("$packageIndex", SqliteType.Integer);
                var relationshipTypeParameter = itemCommand.Parameters.Add("$relationshipType", SqliteType.Text);
                var groupOrdinalParameter = itemCommand.Parameters.Add("$groupOrdinal", SqliteType.Integer);
                var itemOrdinalParameter = itemCommand.Parameters.Add("$itemOrdinal", SqliteType.Integer);
                var updateIdParameter = itemCommand.Parameters.Add("$updateId", SqliteType.Text);
                var revisionParameter = itemCommand.Parameters.Add("$revisionNumber", SqliteType.Integer);

                foreach (var item in record.RelationshipItems)
                {
                    packageParameter.Value = packageIndex;
                    relationshipTypeParameter.Value = item.RelationshipType;
                    groupOrdinalParameter.Value = item.GroupOrdinal;
                    itemOrdinalParameter.Value = item.ItemOrdinal;
                    updateIdParameter.Value = item.UpdateId.ToString("D");
                    revisionParameter.Value = item.RevisionNumber.HasValue
                        ? item.RevisionNumber.Value
                        : DBNull.Value;
                    itemCommand.ExecuteNonQuery();
                }
            }

            using var extraCommand = Connection.CreateCommand();
            extraCommand.Transaction = transaction;
            extraCommand.CommandText = @"
INSERT INTO package_relationship_extra_elements(package_index, ordinal, xml)
VALUES ($packageIndex, $ordinal, $xml);";
            var extraPackageParameter = extraCommand.Parameters.Add("$packageIndex", SqliteType.Integer);
            var extraOrdinalParameter = extraCommand.Parameters.Add("$ordinal", SqliteType.Integer);
            var extraXmlParameter = extraCommand.Parameters.Add("$xml", SqliteType.Text);
            for (var index = 0; index < record.RelationshipExtraElements.Count; index++)
            {
                extraPackageParameter.Value = packageIndex;
                extraOrdinalParameter.Value = index;
                extraXmlParameter.Value = record.RelationshipExtraElements[index];
                extraCommand.ExecuteNonQuery();
            }
        }

        private void InsertDriverMetadata(
            int packageIndex,
            IReadOnlyList<DriverMetadata> metadataEntries,
            SqliteTransaction transaction)
        {
            if (metadataEntries == null || metadataEntries.Count == 0)
            {
                return;
            }

            for (var ordinal = 0; ordinal < metadataEntries.Count; ordinal++)
            {
                var metadata = metadataEntries[ordinal];
                var releaseFingerprint = ComputeDriverReleaseFingerprint(metadata);
                var releaseId = GetOrCreateDriverRelease(metadata, releaseFingerprint, transaction);
                var metadataFingerprint = ComputeDriverMetadataFingerprint(metadata, releaseFingerprint);

                using (var insertCommand = Connection.CreateCommand())
                {
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = @"
INSERT INTO driver_metadata(
    fingerprint,
    driver_release_id,
    hardware_id)
VALUES (
    $fingerprint,
    $releaseId,
    $hardwareId)
ON CONFLICT(fingerprint) DO NOTHING;";
                    insertCommand.Parameters.AddWithValue("$fingerprint", metadataFingerprint);
                    insertCommand.Parameters.AddWithValue("$releaseId", releaseId);
                    insertCommand.Parameters.AddWithValue(
                        "$hardwareId",
                        (object)NormalizeOptionalString(metadata.HardwareID) ?? DBNull.Value);
                    insertCommand.ExecuteNonQuery();
                }

                long metadataId;
                using (var selectCommand = Connection.CreateCommand())
                {
                    selectCommand.Transaction = transaction;
                    selectCommand.CommandText = @"
SELECT driver_metadata_id
FROM driver_metadata
WHERE fingerprint = $fingerprint;";
                    selectCommand.Parameters.AddWithValue("$fingerprint", metadataFingerprint);
                    var result = selectCommand.ExecuteScalar();
                    if (result == null || result == DBNull.Value)
                    {
                        throw new InvalidDataException("Cannot resolve the stored driver metadata record");
                    }

                    metadataId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
                }

                InsertFeatureScores(metadataId, metadata.FeatureScores, transaction);
                InsertComputerHardwareIds(metadataId, "distribution", metadata.DistributionComputerHardwareId, transaction);
                InsertComputerHardwareIds(metadataId, "target", metadata.TargetComputerHardwareId, transaction);

                using var mapCommand = Connection.CreateCommand();
                mapCommand.Transaction = transaction;
                mapCommand.CommandText = @"
INSERT INTO package_driver_metadata(package_index, ordinal, driver_metadata_id)
VALUES ($packageIndex, $ordinal, $metadataId);";
                mapCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                mapCommand.Parameters.AddWithValue("$ordinal", ordinal);
                mapCommand.Parameters.AddWithValue("$metadataId", metadataId);
                mapCommand.ExecuteNonQuery();
            }
        }

        private long GetOrCreateDriverRelease(
            DriverMetadata metadata,
            string fingerprint,
            SqliteTransaction transaction)
        {
            using (var insertCommand = Connection.CreateCommand())
            {
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = @"
INSERT INTO driver_releases(
    fingerprint,
    whql_driver_id,
    manufacturer,
    company,
    provider,
    driver_date,
    driver_version,
    driver_class)
VALUES (
    $fingerprint,
    $whqlDriverId,
    $manufacturer,
    $company,
    $provider,
    $driverDate,
    $driverVersion,
    $driverClass)
ON CONFLICT(fingerprint) DO NOTHING;";
                insertCommand.Parameters.AddWithValue("$fingerprint", fingerprint);
                insertCommand.Parameters.AddWithValue(
                    "$whqlDriverId",
                    (object)NormalizeOptionalString(metadata.WhqlDriverID) ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue(
                    "$manufacturer",
                    (object)NormalizeOptionalString(metadata.Manufacturer) ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue(
                    "$company",
                    (object)NormalizeOptionalString(metadata.Company) ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue(
                    "$provider",
                    (object)NormalizeOptionalString(metadata.Provider) ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue(
                    "$driverDate",
                    metadata.Versioning?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "0001-01-01");
                insertCommand.Parameters.AddWithValue(
                    "$driverVersion",
                    (metadata.Versioning?.Version ?? 0UL).ToString(CultureInfo.InvariantCulture));
                insertCommand.Parameters.AddWithValue(
                    "$driverClass",
                    (object)NormalizeOptionalString(metadata.Class) ?? DBNull.Value);
                insertCommand.ExecuteNonQuery();
            }

            using var selectCommand = Connection.CreateCommand();
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = @"
SELECT driver_release_id
FROM driver_releases
WHERE fingerprint = $fingerprint;";
            selectCommand.Parameters.AddWithValue("$fingerprint", fingerprint);
            var result = selectCommand.ExecuteScalar();
            if (result == null || result == DBNull.Value)
            {
                throw new InvalidDataException("Cannot resolve the stored driver release record");
            }

            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        private static string ComputeDriverReleaseFingerprint(DriverMetadata metadata)
        {
            var canonical = new StringBuilder();
            AppendFingerprintValue(canonical, NormalizeOptionalString(metadata.WhqlDriverID));
            AppendFingerprintValue(canonical, NormalizeOptionalString(metadata.Manufacturer));
            AppendFingerprintValue(canonical, NormalizeOptionalString(metadata.Company));
            AppendFingerprintValue(canonical, NormalizeOptionalString(metadata.Provider));
            AppendFingerprintValue(
                canonical,
                metadata.Versioning?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            AppendFingerprintValue(
                canonical,
                (metadata.Versioning?.Version ?? 0UL).ToString(CultureInfo.InvariantCulture));
            AppendFingerprintValue(canonical, NormalizeOptionalString(metadata.Class));
            return ComputeSha256(canonical);
        }

        private static string ComputeDriverMetadataFingerprint(
            DriverMetadata metadata,
            string releaseFingerprint)
        {
            var canonical = new StringBuilder();
            AppendFingerprintValue(canonical, releaseFingerprint);
            AppendFingerprintValue(canonical, NormalizeOptionalString(metadata.HardwareID));

            foreach (var score in metadata.FeatureScores ?? Enumerable.Empty<DriverFeatureScore>())
            {
                AppendFingerprintValue(canonical, NormalizeOptionalString(score.OperatingSystem));
                AppendFingerprintValue(canonical, score.Score.ToString(CultureInfo.InvariantCulture));
            }

            canonical.Append('|');
            foreach (var hardwareId in metadata.DistributionComputerHardwareId ?? Enumerable.Empty<Guid>())
            {
                AppendFingerprintValue(canonical, hardwareId.ToString("D"));
            }

            canonical.Append('|');
            foreach (var hardwareId in metadata.TargetComputerHardwareId ?? Enumerable.Empty<Guid>())
            {
                AppendFingerprintValue(canonical, hardwareId.ToString("D"));
            }

            return ComputeSha256(canonical);
        }

        private static string ComputeSha256(StringBuilder value)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value.ToString())))
                .ToLowerInvariant();
        }

        private static string NormalizeOptionalString(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static void AppendFingerprintValue(StringBuilder builder, string value)
        {
            if (value == null)
            {
                builder.Append("-1:");
                return;
            }

            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
        }

        private void InsertFeatureScores(
            long metadataId,
            IReadOnlyList<DriverFeatureScore> featureScores,
            SqliteTransaction transaction)
        {
            if (featureScores == null || featureScores.Count == 0)
            {
                return;
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO driver_feature_scores(driver_metadata_id, ordinal, operating_system, score)
VALUES ($metadataId, $ordinal, $operatingSystem, $score)
ON CONFLICT(driver_metadata_id, ordinal) DO NOTHING;";
            var metadataParameter = command.Parameters.Add("$metadataId", SqliteType.Integer);
            var ordinalParameter = command.Parameters.Add("$ordinal", SqliteType.Integer);
            var operatingSystemParameter = command.Parameters.Add("$operatingSystem", SqliteType.Text);
            var scoreParameter = command.Parameters.Add("$score", SqliteType.Integer);

            for (var ordinal = 0; ordinal < featureScores.Count; ordinal++)
            {
                metadataParameter.Value = metadataId;
                ordinalParameter.Value = ordinal;
                operatingSystemParameter.Value = (object)NormalizeOptionalString(featureScores[ordinal].OperatingSystem) ?? DBNull.Value;
                scoreParameter.Value = featureScores[ordinal].Score;
                command.ExecuteNonQuery();
            }
        }

        private void InsertComputerHardwareIds(
            long metadataId,
            string kind,
            IReadOnlyList<Guid> hardwareIds,
            SqliteTransaction transaction)
        {
            if (hardwareIds == null || hardwareIds.Count == 0)
            {
                return;
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO driver_computer_hardware_ids(driver_metadata_id, kind, ordinal, hardware_id)
VALUES ($metadataId, $kind, $ordinal, $hardwareId)
ON CONFLICT(driver_metadata_id, kind, ordinal) DO NOTHING;";
            var metadataParameter = command.Parameters.Add("$metadataId", SqliteType.Integer);
            var kindParameter = command.Parameters.Add("$kind", SqliteType.Text);
            var ordinalParameter = command.Parameters.Add("$ordinal", SqliteType.Integer);
            var hardwareIdParameter = command.Parameters.Add("$hardwareId", SqliteType.Text);

            for (var ordinal = 0; ordinal < hardwareIds.Count; ordinal++)
            {
                metadataParameter.Value = metadataId;
                kindParameter.Value = kind;
                ordinalParameter.Value = ordinal;
                hardwareIdParameter.Value = hardwareIds[ordinal].ToString("D");
                command.ExecuteNonQuery();
            }
        }

        private void InsertRelationalFiles(
            int packageIndex,
            IReadOnlyList<UpdateFile> files,
            SqliteTransaction transaction)
        {
            for (var fileOrdinal = 0; fileOrdinal < files.Count; fileOrdinal++)
            {
                var file = files[fileOrdinal];
                var sha1 = file.Digests?
                    .FirstOrDefault(digest => string.Equals(digest.Algorithm, "SHA1", StringComparison.OrdinalIgnoreCase));
                if (sha1 == null || string.IsNullOrWhiteSpace(sha1.DigestBase64))
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

                var preferredMuUrl = file.Urls?
                    .FirstOrDefault(url =>
                        string.Equals(url.DigestBase64, sha1.DigestBase64, StringComparison.Ordinal) &&
                        !string.IsNullOrEmpty(url.MuUrl))?
                    .MuUrl
                    ?? file.Urls?.FirstOrDefault(url => !string.IsNullOrEmpty(url.MuUrl))?.MuUrl;
                var preferredUssUrl = file.Urls?
                    .FirstOrDefault(url =>
                        string.Equals(url.DigestBase64, sha1.DigestBase64, StringComparison.Ordinal) &&
                        !string.IsNullOrEmpty(url.UssUrl))?
                    .UssUrl
                    ?? file.Urls?.FirstOrDefault(url => !string.IsNullOrEmpty(url.UssUrl))?.UssUrl;
                var modifiedValue = file.ModifiedDate == default
                    ? null
                    : file.ModifiedDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

                using (var fileCommand = Connection.CreateCommand())
                {
                    fileCommand.Transaction = transaction;
                    fileCommand.CommandText = @"
INSERT INTO file_locations(
    sha1_base64,
    sha1,
    sha1_hex,
    mu_url,
    file_name,
    file_json,
    file_blob,
    file_compression,
    package_index,
    size,
    modified,
    patching_type,
    uss_url)
VALUES (
    $sha1Base64,
    $sha1,
    $sha1Hex,
    $muUrl,
    $fileName,
    NULL,
    NULL,
    NULL,
    $packageIndex,
    $size,
    $modified,
    $patchingType,
    $ussUrl)
ON CONFLICT(sha1_base64) DO UPDATE SET
    sha1 = excluded.sha1,
    sha1_hex = excluded.sha1_hex,
    mu_url = COALESCE(excluded.mu_url, file_locations.mu_url),
    uss_url = COALESCE(excluded.uss_url, file_locations.uss_url),
    file_name = COALESCE(excluded.file_name, file_locations.file_name),
    size = COALESCE(excluded.size, file_locations.size),
    modified = COALESCE(excluded.modified, file_locations.modified),
    patching_type = COALESCE(excluded.patching_type, file_locations.patching_type),
    file_json = NULL,
    file_blob = NULL,
    file_compression = NULL;";
                    fileCommand.Parameters.AddWithValue("$sha1Base64", sha1.DigestBase64);
                    fileCommand.Parameters.AddWithValue("$sha1", sha1Bytes);
                    fileCommand.Parameters.AddWithValue("$sha1Hex", Convert.ToHexString(sha1Bytes));
                    fileCommand.Parameters.AddWithValue("$muUrl", (object)preferredMuUrl ?? DBNull.Value);
                    fileCommand.Parameters.AddWithValue("$fileName", (object)file.FileName ?? DBNull.Value);
                    fileCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                    fileCommand.Parameters.AddWithValue("$size", checked((long)file.Size));
                    fileCommand.Parameters.AddWithValue(
                        "$modified",
                        (object)modifiedValue ?? DBNull.Value);
                    fileCommand.Parameters.AddWithValue("$patchingType", (object)file.PatchingType ?? DBNull.Value);
                    fileCommand.Parameters.AddWithValue("$ussUrl", (object)preferredUssUrl ?? DBNull.Value);
                    fileCommand.ExecuteNonQuery();
                }

                using (var digestCommand = Connection.CreateCommand())
                {
                    digestCommand.Transaction = transaction;
                    digestCommand.CommandText = @"
INSERT INTO file_digests(sha1_base64, ordinal, algorithm, digest_base64)
VALUES ($sha1Base64, $ordinal, $algorithm, $digestBase64)
ON CONFLICT(sha1_base64, algorithm, digest_base64) DO UPDATE SET
    ordinal = MIN(file_digests.ordinal, excluded.ordinal);";
                    var sha1Parameter = digestCommand.Parameters.Add("$sha1Base64", SqliteType.Text);
                    var ordinalParameter = digestCommand.Parameters.Add("$ordinal", SqliteType.Integer);
                    var algorithmParameter = digestCommand.Parameters.Add("$algorithm", SqliteType.Text);
                    var digestParameter = digestCommand.Parameters.Add("$digestBase64", SqliteType.Text);

                    var digests = file.Digests ?? new List<ContentFileDigest>();
                    for (var digestOrdinal = 0; digestOrdinal < digests.Count; digestOrdinal++)
                    {
                        sha1Parameter.Value = sha1.DigestBase64;
                        ordinalParameter.Value = digestOrdinal;
                        algorithmParameter.Value = digests[digestOrdinal].Algorithm ?? string.Empty;
                        digestParameter.Value = digests[digestOrdinal].DigestBase64 ?? string.Empty;
                        digestCommand.ExecuteNonQuery();
                    }
                }

                using (var mapCommand = Connection.CreateCommand())
                {
                    mapCommand.Transaction = transaction;
                    mapCommand.CommandText = @"
INSERT INTO package_file_map(
    package_index,
    sha1_base64,
    ordinal,
    file_name,
    size,
    modified,
    patching_type)
VALUES (
    $packageIndex,
    $sha1Base64,
    $ordinal,
    $fileName,
    $size,
    $modified,
    $patchingType)
ON CONFLICT(package_index, sha1_base64) DO UPDATE SET
    ordinal = excluded.ordinal,
    file_name = excluded.file_name,
    size = excluded.size,
    modified = excluded.modified,
    patching_type = excluded.patching_type;";
                    mapCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                    mapCommand.Parameters.AddWithValue("$sha1Base64", sha1.DigestBase64);
                    mapCommand.Parameters.AddWithValue("$ordinal", fileOrdinal);
                    mapCommand.Parameters.AddWithValue("$fileName", (object)file.FileName ?? DBNull.Value);
                    mapCommand.Parameters.AddWithValue("$size", checked((long)file.Size));
                    mapCommand.Parameters.AddWithValue("$modified", (object)modifiedValue ?? DBNull.Value);
                    mapCommand.Parameters.AddWithValue("$patchingType", (object)file.PatchingType ?? DBNull.Value);
                    mapCommand.ExecuteNonQuery();
                }
            }
        }

        private byte[] BuildRelationalMetadataBytes(int packageIndex)
        {
            Guid updateId;
            int revisionNumber;
            using (var identityCommand = Connection.CreateCommand())
            {
                identityCommand.CommandText = @"
SELECT update_id, revision_number
FROM packages
WHERE package_index = $packageIndex;";
                identityCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = identityCommand.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    throw new InvalidDataException($"Relational package {packageIndex} has no update identity");
                }

                updateId = Guid.Parse(reader.GetString(0));
                revisionNumber = reader.GetInt32(1);
            }

            var root = new XElement(
                UpdateNamespace + "Update",
                new XAttribute(XNamespace.Xmlns + "drv", DriverNamespace),
                new XAttribute(XNamespace.Xmlns + "cat", "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/Category"),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XAttribute(XNamespace.Xmlns + "b", "http://schemas.microsoft.com/msus/2002/12/BaseApplicabilityRules"),
                new XAttribute(XNamespace.Xmlns + "m", "http://schemas.microsoft.com/msus/2002/12/MsiApplicabilityRules"),
                new XAttribute(XNamespace.Xmlns + "cmd", "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/CommandLineInstallation"),
                new XAttribute(XNamespace.Xmlns + "psf", "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/WindowsPatch"),
                new XAttribute(XNamespace.Xmlns + "cbs", "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/Cbs"),
                new XAttribute(XNamespace.Xmlns + "msp", "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/WindowsInstaller"),
                new XAttribute(XNamespace.Xmlns + "wsi", "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/WindowsSetup"));

            root.Add(new XElement(
                UpdateNamespace + "UpdateIdentity",
                new XAttribute("UpdateID", updateId.ToString("D")),
                new XAttribute("RevisionNumber", revisionNumber.ToString(CultureInfo.InvariantCulture))));

            var properties = new XElement(UpdateNamespace + "Properties");
            using (var attributesCommand = Connection.CreateCommand())
            {
                attributesCommand.CommandText = @"
SELECT name, value
FROM package_property_attributes
WHERE package_index = $packageIndex
ORDER BY name;";
                attributesCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = attributesCommand.ExecuteReader();
                while (reader.Read())
                {
                    properties.SetAttributeValue(reader.GetString(0), reader.GetString(1));
                }
            }

            using (var elementsCommand = Connection.CreateCommand())
            {
                elementsCommand.CommandText = @"
SELECT name, value, xml
FROM package_property_elements
WHERE package_index = $packageIndex
ORDER BY ordinal;";
                elementsCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = elementsCommand.ExecuteReader();
                while (reader.Read())
                {
                    properties.Add(CreateStoredElement(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            root.Add(properties);

            var relationships = BuildRelationalRelationshipsElement(packageIndex);
            if (relationships != null)
            {
                root.Add(relationships);
            }

            string applicabilityXml = null;
            string handlerXml = null;
            using (var fragmentsCommand = Connection.CreateCommand())
            {
                fragmentsCommand.CommandText = @"
SELECT COALESCE(applicability.xml, fragments.applicability_template_xml),
       COALESCE(handler.xml, fragments.handler_specific_xml)
FROM package_fragments AS fragments
LEFT JOIN xml_fragments AS applicability
  ON applicability.fragment_hash = fragments.applicability_hash
LEFT JOIN xml_fragments AS handler
  ON handler.fragment_hash = fragments.handler_hash
WHERE fragments.package_index = $packageIndex;";
                fragmentsCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = fragmentsCommand.ExecuteReader();
                if (reader.Read())
                {
                    applicabilityXml = reader.IsDBNull(0) ? null : reader.GetString(0);
                    handlerXml = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            XElement applicability = null;
            if (!string.IsNullOrWhiteSpace(applicabilityXml))
            {
                applicability = ParseStoredElement(applicabilityXml, "ApplicabilityRules");
            }

            var driverMetadata = ReadRelationalDriverMetadata(packageIndex);
            if (driverMetadata.Count > 0)
            {
                applicability ??= new XElement(UpdateNamespace + "ApplicabilityRules");
                var metadataElement = applicability.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "Metadata");
                if (metadataElement == null)
                {
                    metadataElement = new XElement(UpdateNamespace + "Metadata");
                    applicability.AddFirst(metadataElement);
                }

                foreach (var metadata in driverMetadata)
                {
                    metadataElement.Add(CreateDriverMetadataElement(metadata));
                }
            }

            if (applicability != null)
            {
                root.Add(applicability);
            }

            var files = ReadRelationalFiles(packageIndex);
            if (files.Count > 0)
            {
                var filesElement = new XElement(UpdateNamespace + "Files");
                foreach (var file in files)
                {
                    filesElement.Add(CreateFileElement(file));
                }

                root.Add(filesElement);
            }

            if (!string.IsNullOrWhiteSpace(handlerXml))
            {
                root.Add(ParseStoredElement(handlerXml, "HandlerSpecificData"));
            }

            var localizedCollection = BuildRelationalLocalizedPropertiesElement(packageIndex);
            if (localizedCollection != null)
            {
                root.Add(localizedCollection);
            }

            var document = new XDocument(new XDeclaration("1.0", "utf-16", null), root);
            return Encoding.Unicode.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        }

        private static XElement CreateStoredElement(string name, string value, string xml)
        {
            if (!string.IsNullOrWhiteSpace(xml))
            {
                return ParseStoredElement(xml, name);
            }

            return new XElement(UpdateNamespace + name, value ?? string.Empty);
        }

        private static XElement ParseStoredElement(string xml, string expectedLocalName)
        {
            var element = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
            if (element.Name.Namespace == XNamespace.None)
            {
                element.Name = UpdateNamespace + element.Name.LocalName;
            }

            if (!string.IsNullOrEmpty(expectedLocalName) &&
                !string.Equals(element.Name.LocalName, expectedLocalName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Stored XML element '{element.Name.LocalName}' does not match '{expectedLocalName}'");
            }

            return element;
        }

        private XElement BuildRelationalRelationshipsElement(int packageIndex)
        {
            var groups = new List<RelationalRelationshipGroup>();
            using (var groupCommand = Connection.CreateCommand())
            {
                groupCommand.CommandText = @"
SELECT relationship_type, group_ordinal, group_kind, is_category
FROM package_relationship_groups
WHERE package_index = $packageIndex
ORDER BY CASE relationship_type
           WHEN 'Prerequisites' THEN 1
           WHEN 'SupersededUpdates' THEN 2
           WHEN 'BundledUpdates' THEN 3
           ELSE 4
         END,
         group_ordinal;";
                groupCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = groupCommand.ExecuteReader();
                while (reader.Read())
                {
                    groups.Add(new RelationalRelationshipGroup
                    {
                        RelationshipType = reader.GetString(0),
                        GroupOrdinal = reader.GetInt32(1),
                        GroupKind = reader.GetString(2),
                        IsCategory = reader.GetInt32(3) != 0
                    });
                }
            }

            var items = new List<RelationalRelationshipItem>();
            using (var itemCommand = Connection.CreateCommand())
            {
                itemCommand.CommandText = @"
SELECT relationship_type, group_ordinal, item_ordinal, update_id, revision_number
FROM package_relationship_items
WHERE package_index = $packageIndex
ORDER BY relationship_type, group_ordinal, item_ordinal;";
                itemCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = itemCommand.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(new RelationalRelationshipItem
                    {
                        RelationshipType = reader.GetString(0),
                        GroupOrdinal = reader.GetInt32(1),
                        ItemOrdinal = reader.GetInt32(2),
                        UpdateId = Guid.Parse(reader.GetString(3)),
                        RevisionNumber = reader.IsDBNull(4) ? null : reader.GetInt32(4)
                    });
                }
            }

            var extras = new List<XElement>();
            using (var extraCommand = Connection.CreateCommand())
            {
                extraCommand.CommandText = @"
SELECT xml
FROM package_relationship_extra_elements
WHERE package_index = $packageIndex
ORDER BY ordinal;";
                extraCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = extraCommand.ExecuteReader();
                while (reader.Read())
                {
                    extras.Add(ParseStoredElement(reader.GetString(0), null));
                }
            }

            if (groups.Count == 0 && extras.Count == 0)
            {
                return null;
            }

            var relationships = new XElement(UpdateNamespace + "Relationships");
            foreach (var relationGroup in groups.GroupBy(group => group.RelationshipType))
            {
                var relation = new XElement(UpdateNamespace + relationGroup.Key);
                foreach (var group in relationGroup.OrderBy(group => group.GroupOrdinal))
                {
                    var groupItems = items
                        .Where(item =>
                            item.RelationshipType == group.RelationshipType &&
                            item.GroupOrdinal == group.GroupOrdinal)
                        .OrderBy(item => item.ItemOrdinal)
                        .ToList();

                    if (string.Equals(group.GroupKind, RelationalMetadataExtractor.AtLeastOneGroup, StringComparison.Ordinal))
                    {
                        var atLeastOne = new XElement(UpdateNamespace + "AtLeastOne");
                        if (group.IsCategory)
                        {
                            atLeastOne.SetAttributeValue("IsCategory", "true");
                        }

                        foreach (var item in groupItems)
                        {
                            atLeastOne.Add(CreateUpdateIdentityElement(item));
                        }

                        relation.Add(atLeastOne);
                    }
                    else
                    {
                        foreach (var item in groupItems)
                        {
                            relation.Add(CreateUpdateIdentityElement(item));
                        }
                    }
                }

                relationships.Add(relation);
            }

            foreach (var extra in extras)
            {
                relationships.Add(extra);
            }

            return relationships;
        }

        private static XElement CreateUpdateIdentityElement(RelationalRelationshipItem item)
        {
            var identity = new XElement(
                UpdateNamespace + "UpdateIdentity",
                new XAttribute("UpdateID", item.UpdateId.ToString("D")));
            if (item.RevisionNumber.HasValue)
            {
                identity.SetAttributeValue(
                    "RevisionNumber",
                    item.RevisionNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            return identity;
        }

        private XElement BuildRelationalLocalizedPropertiesElement(int packageIndex)
        {
            var elements = new List<RelationalLocalizedElement>();
            using var command = Connection.CreateCommand();
            command.CommandText = @"
SELECT language, ordinal, name, value, xml
FROM package_localized_elements
WHERE package_index = $packageIndex
ORDER BY CASE language WHEN 'en' THEN 0 ELSE 1 END, language, ordinal;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                elements.Add(new RelationalLocalizedElement
                {
                    Language = reader.GetString(0),
                    Ordinal = reader.GetInt32(1),
                    Name = reader.GetString(2),
                    Value = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Xml = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }

            if (elements.Count == 0)
            {
                return null;
            }

            var collection = new XElement(UpdateNamespace + "LocalizedPropertiesCollection");
            foreach (var languageGroup in elements.GroupBy(element => element.Language))
            {
                var localized = new XElement(
                    UpdateNamespace + "LocalizedProperties",
                    new XElement(UpdateNamespace + "Language", languageGroup.Key));
                foreach (var element in languageGroup.OrderBy(element => element.Ordinal))
                {
                    localized.Add(CreateStoredElement(element.Name, element.Value, element.Xml));
                }

                collection.Add(localized);
            }

            return collection;
        }

        private static XElement CreateDriverMetadataElement(DriverMetadata metadata)
        {
            var element = new XElement(DriverNamespace + "WindowsDriverMetaData");
            AddAttributeIfPresent(element, "HardwareID", metadata.HardwareID);
            AddAttributeIfPresent(element, "WhqlDriverID", metadata.WhqlDriverID);
            AddAttributeIfPresent(element, "Manufacturer", metadata.Manufacturer);
            AddAttributeIfPresent(element, "Company", metadata.Company);
            AddAttributeIfPresent(element, "Provider", metadata.Provider);
            if (metadata.Versioning != null)
            {
                element.SetAttributeValue(
                    "DriverVerDate",
                    metadata.Versioning.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                element.SetAttributeValue("DriverVerVersion", metadata.Versioning.VersionString);
            }

            AddAttributeIfPresent(element, "Class", metadata.Class);

            foreach (var featureScore in metadata.FeatureScores ?? new List<DriverFeatureScore>())
            {
                var scoreElement = new XElement(
                    DriverNamespace + "FeatureScore",
                    new XAttribute(
                        "FeatureScore",
                        featureScore.Score.ToString("X2", CultureInfo.InvariantCulture)));
                AddAttributeIfPresent(scoreElement, "OperatingSystem", featureScore.OperatingSystem);
                element.Add(scoreElement);
            }

            foreach (var hardwareId in metadata.DistributionComputerHardwareId ?? new List<Guid>())
            {
                element.Add(new XElement(
                    DriverNamespace + "DistributionComputerHardwareId",
                    hardwareId.ToString("D")));
            }

            foreach (var hardwareId in metadata.TargetComputerHardwareId ?? new List<Guid>())
            {
                element.Add(new XElement(
                    DriverNamespace + "TargetComputerHardwareId",
                    hardwareId.ToString("D")));
            }

            return element;
        }

        private static void AddAttributeIfPresent(XElement element, string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                element.SetAttributeValue(name, value);
            }
        }

        private static XElement CreateFileElement(UpdateFile file)
        {
            var digests = file.Digests ?? new List<ContentFileDigest>();
            var primaryDigest = digests.FirstOrDefault()
                ?? throw new InvalidDataException($"File '{file.FileName}' has no digest");
            var element = new XElement(
                UpdateNamespace + "File",
                new XAttribute("DigestAlgorithm", primaryDigest.Algorithm ?? "SHA1"),
                new XAttribute("Digest", primaryDigest.DigestBase64 ?? string.Empty),
                new XAttribute("FileName", file.FileName ?? string.Empty),
                new XAttribute(
                    "Modified",
                    file.ModifiedDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                new XAttribute("Size", file.Size.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("PatchingType", file.PatchingType ?? string.Empty));

            foreach (var digest in digests.Skip(1))
            {
                element.Add(new XElement(
                    UpdateNamespace + "AdditionalDigest",
                    new XAttribute("Algorithm", digest.Algorithm ?? string.Empty),
                    digest.DigestBase64 ?? string.Empty));
            }

            return element;
        }

        private List<UpdateFile> ReadRelationalFiles(int packageIndex)
        {
            var rows = new Dictionary<string, RelationalFileReadRow>(StringComparer.Ordinal);
            using var command = Connection.CreateCommand();
            command.CommandText = @"
SELECT map.ordinal,
       location.sha1_base64,
       COALESCE(map.file_name, location.file_name),
       COALESCE(map.size, location.size),
       COALESCE(map.modified, location.modified),
       COALESCE(map.patching_type, location.patching_type),
       location.mu_url,
       location.uss_url,
       digest.ordinal,
       digest.algorithm,
       digest.digest_base64
FROM package_file_map AS map
JOIN file_locations AS location
  ON location.sha1_base64 = map.sha1_base64
LEFT JOIN file_digests AS digest
  ON digest.sha1_base64 = location.sha1_base64
WHERE map.package_index = $packageIndex
ORDER BY map.ordinal, location.sha1_base64, digest.ordinal;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sha1 = reader.GetString(1);
                if (!rows.TryGetValue(sha1, out var row))
                {
                    DateTime modified = default;
                    if (!reader.IsDBNull(4))
                    {
                        DateTime.TryParse(
                            reader.GetString(4),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out modified);
                    }

                    row = new RelationalFileReadRow
                    {
                        Ordinal = reader.GetInt32(0),
                        Sha1Base64 = sha1,
                        FileName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Size = reader.IsDBNull(3) ? 0UL : checked((ulong)reader.GetInt64(3)),
                        Modified = modified,
                        PatchingType = reader.IsDBNull(5) ? null : reader.GetString(5),
                        MuUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                        UssUrl = reader.IsDBNull(7) ? null : reader.GetString(7)
                    };
                    rows.Add(sha1, row);
                }

                if (!reader.IsDBNull(8))
                {
                    row.Digests.Add(new ContentFileDigest(
                        reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                        reader.IsDBNull(10) ? string.Empty : reader.GetString(10)));
                }
            }

            var result = new List<UpdateFile>();
            foreach (var row in rows.Values.OrderBy(row => row.Ordinal))
            {
                var sha1Digest = row.Digests.FirstOrDefault(digest =>
                    string.Equals(digest.Algorithm, "SHA1", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(digest.DigestBase64, row.Sha1Base64, StringComparison.Ordinal));
                sha1Digest ??= new ContentFileDigest("SHA1", row.Sha1Base64);

                var orderedDigests = new List<ContentFileDigest> { sha1Digest };
                orderedDigests.AddRange(row.Digests.Where(digest => !ReferenceEquals(digest, sha1Digest)));

                var urls = new List<UpdateFileUrl>();
                if (!string.IsNullOrEmpty(row.MuUrl) || !string.IsNullOrEmpty(row.UssUrl))
                {
                    urls.Add(new UpdateFileUrl(row.Sha1Base64, row.MuUrl, row.UssUrl));
                }

                result.Add(new UpdateFile
                {
                    FileName = row.FileName,
                    Size = row.Size,
                    ModifiedDate = row.Modified,
                    PatchingType = row.PatchingType,
                    Digests = orderedDigests,
                    Urls = urls
                });
            }

            return result;
        }

        private UpdateFile ReadRelationalFileBySha1(string sha1Base64)
        {
            string fileName;
            ulong size;
            DateTime modified = default;
            string patchingType;
            string muUrl;
            string ussUrl;

            using (var command = Connection.CreateCommand())
            {
                command.CommandText = @"
SELECT file_name,
       size,
       modified,
       patching_type,
       mu_url,
       uss_url
FROM file_locations
WHERE sha1_base64 = $sha1;";
                command.Parameters.AddWithValue("$sha1", sha1Base64);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                fileName = reader.IsDBNull(0) ? null : reader.GetString(0);
                size = reader.IsDBNull(1) ? 0UL : checked((ulong)reader.GetInt64(1));
                if (!reader.IsDBNull(2))
                {
                    DateTime.TryParse(
                        reader.GetString(2),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out modified);
                }

                patchingType = reader.IsDBNull(3) ? null : reader.GetString(3);
                muUrl = reader.IsDBNull(4) ? null : reader.GetString(4);
                ussUrl = reader.IsDBNull(5) ? null : reader.GetString(5);
            }

            var digests = new List<ContentFileDigest>();
            using (var digestCommand = Connection.CreateCommand())
            {
                digestCommand.CommandText = @"
SELECT algorithm, digest_base64
FROM file_digests
WHERE sha1_base64 = $sha1
ORDER BY ordinal;";
                digestCommand.Parameters.AddWithValue("$sha1", sha1Base64);
                using var digestReader = digestCommand.ExecuteReader();
                while (digestReader.Read())
                {
                    digests.Add(new ContentFileDigest(digestReader.GetString(0), digestReader.GetString(1)));
                }
            }

            var sha1Digest = digests.FirstOrDefault(digest =>
                string.Equals(digest.Algorithm, "SHA1", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(digest.DigestBase64, sha1Base64, StringComparison.Ordinal));
            sha1Digest ??= new ContentFileDigest("SHA1", sha1Base64);
            var orderedDigests = new List<ContentFileDigest> { sha1Digest };
            orderedDigests.AddRange(digests.Where(digest => !ReferenceEquals(digest, sha1Digest)));

            var urls = new List<UpdateFileUrl>();
            if (!string.IsNullOrEmpty(muUrl) || !string.IsNullOrEmpty(ussUrl))
            {
                urls.Add(new UpdateFileUrl(sha1Base64, muUrl, ussUrl));
            }

            return new UpdateFile
            {
                FileName = fileName,
                Size = size,
                ModifiedDate = modified,
                PatchingType = patchingType,
                Digests = orderedDigests,
                Urls = urls
            };
        }

        private List<DriverMetadata> ReadRelationalDriverMetadata(int packageIndex)
        {
            var rows = new List<RelationalDriverReadRow>();
            using (var command = Connection.CreateCommand())
            {
                command.CommandText = @"
SELECT metadata.driver_metadata_id,
       metadata.hardware_id,
       release.whql_driver_id,
       release.manufacturer,
       release.company,
       release.provider,
       release.driver_date,
       release.driver_version,
       release.driver_class
FROM package_driver_metadata AS mapping
JOIN driver_metadata AS metadata
  ON metadata.driver_metadata_id = mapping.driver_metadata_id
JOIN driver_releases AS release
  ON release.driver_release_id = metadata.driver_release_id
WHERE mapping.package_index = $packageIndex
ORDER BY mapping.ordinal;";
                command.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var date = DateTime.MinValue;
                    DateTime.TryParseExact(
                        reader.GetString(6),
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out date);
                    ulong.TryParse(reader.GetString(7), NumberStyles.None, CultureInfo.InvariantCulture, out var version);
                    rows.Add(new RelationalDriverReadRow
                    {
                        Id = reader.GetInt64(0),
                        HardwareId = reader.IsDBNull(1) ? null : reader.GetString(1),
                        WhqlDriverId = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Manufacturer = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Company = reader.IsDBNull(4) ? null : reader.GetString(4),
                        Provider = reader.IsDBNull(5) ? null : reader.GetString(5),
                        DriverDate = date,
                        DriverVersion = version,
                        DriverClass = reader.IsDBNull(8) ? null : reader.GetString(8)
                    });
                }
            }

            if (rows.Count == 0)
            {
                return new List<DriverMetadata>();
            }

            var rowsById = rows
                .GroupBy(row => row.Id)
                .ToDictionary(group => group.Key, group => group.ToList());
            using (var scoreCommand = Connection.CreateCommand())
            {
                scoreCommand.CommandText = @"
SELECT score.driver_metadata_id, score.operating_system, score.score
FROM driver_feature_scores AS score
WHERE EXISTS (
    SELECT 1
    FROM package_driver_metadata AS mapping
    WHERE mapping.package_index = $packageIndex
      AND mapping.driver_metadata_id = score.driver_metadata_id)
ORDER BY score.driver_metadata_id, score.ordinal;";
                scoreCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = scoreCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (rowsById.TryGetValue(reader.GetInt64(0), out var matchingRows))
                    {
                        foreach (var row in matchingRows)
                        {
                            row.FeatureScores.Add(new DriverFeatureScore
                            {
                                OperatingSystem = reader.IsDBNull(1) ? null : reader.GetString(1),
                                Score = checked((byte)reader.GetInt32(2))
                            });
                        }
                    }
                }
            }

            using (var hardwareCommand = Connection.CreateCommand())
            {
                hardwareCommand.CommandText = @"
SELECT hardware.driver_metadata_id, hardware.kind, hardware.hardware_id
FROM driver_computer_hardware_ids AS hardware
WHERE EXISTS (
    SELECT 1
    FROM package_driver_metadata AS mapping
    WHERE mapping.package_index = $packageIndex
      AND mapping.driver_metadata_id = hardware.driver_metadata_id)
ORDER BY hardware.driver_metadata_id, hardware.kind, hardware.ordinal;";
                hardwareCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                using var reader = hardwareCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (!rowsById.TryGetValue(reader.GetInt64(0), out var matchingRows) ||
                        !Guid.TryParse(reader.GetString(2), out var hardwareId))
                    {
                        continue;
                    }

                    foreach (var row in matchingRows)
                    {
                        if (string.Equals(reader.GetString(1), "distribution", StringComparison.Ordinal))
                        {
                            row.DistributionHardwareIds.Add(hardwareId);
                        }
                        else
                        {
                            row.TargetHardwareIds.Add(hardwareId);
                        }
                    }
                }
            }

            return rows.Select(row => new DriverMetadata(
                row.HardwareId,
                row.WhqlDriverId,
                row.Manufacturer,
                row.Company,
                row.Provider,
                row.DriverDate,
                row.DriverVersion,
                row.DriverClass,
                row.FeatureScores,
                row.DistributionHardwareIds,
                row.TargetHardwareIds)).ToList();
        }

        private List<IPrerequisite> ReadRelationalPrerequisites(int packageIndex)
        {
            var groups = ReadRelationalRelationshipGroups(
                packageIndex,
                RelationalMetadataExtractor.PrerequisitesRelationship);
            var prerequisites = new List<IPrerequisite>();
            foreach (var group in groups)
            {
                var ids = group.Items.Select(item => item.UpdateId).ToList();
                if (ids.Count == 0)
                {
                    continue;
                }

                if (string.Equals(group.GroupKind, RelationalMetadataExtractor.AtLeastOneGroup, StringComparison.Ordinal))
                {
                    prerequisites.Add(new AtLeastOne(ids, group.IsCategory));
                }
                else
                {
                    prerequisites.AddRange(ids.Select(id => (IPrerequisite)new Simple(id)));
                }
            }

            return prerequisites;
        }

        private List<Guid> ReadRelationalCategories(int packageIndex)
        {
            return ReadRelationalRelationshipGroups(
                    packageIndex,
                    RelationalMetadataExtractor.PrerequisitesRelationship)
                .Where(group => group.IsCategory)
                .SelectMany(group => group.Items)
                .Select(item => item.UpdateId)
                .Distinct()
                .ToList();
        }

        private List<Guid> ReadRelationalSupersededUpdates(int packageIndex)
        {
            return ReadRelationalRelationshipGroups(
                    packageIndex,
                    RelationalMetadataExtractor.SupersededRelationship)
                .SelectMany(group => group.Items)
                .Select(item => item.UpdateId)
                .Distinct()
                .ToList();
        }

        private List<MicrosoftUpdatePackageIdentity> ReadRelationalBundledUpdates(int packageIndex)
        {
            return ReadRelationalRelationshipGroups(
                    packageIndex,
                    RelationalMetadataExtractor.BundledRelationship)
                .SelectMany(group => group.Items)
                .Where(item => item.RevisionNumber.HasValue)
                .Select(item => new MicrosoftUpdatePackageIdentity(item.UpdateId, item.RevisionNumber.Value))
                .Distinct()
                .ToList();
        }

        private List<RelationalRelationshipReadGroup> ReadRelationalRelationshipGroups(
            int packageIndex,
            string relationshipType)
        {
            var groups = new List<RelationalRelationshipReadGroup>();
            using var command = Connection.CreateCommand();
            command.CommandText = @"
SELECT groups.group_ordinal,
       groups.group_kind,
       groups.is_category,
       items.item_ordinal,
       items.update_id,
       items.revision_number
FROM package_relationship_groups AS groups
LEFT JOIN package_relationship_items AS items
  ON items.package_index = groups.package_index
 AND items.relationship_type = groups.relationship_type
 AND items.group_ordinal = groups.group_ordinal
WHERE groups.package_index = $packageIndex
  AND groups.relationship_type = $relationshipType
ORDER BY groups.group_ordinal, items.item_ordinal;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            command.Parameters.AddWithValue("$relationshipType", relationshipType);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var groupOrdinal = reader.GetInt32(0);
                var group = groups.LastOrDefault(candidate => candidate.GroupOrdinal == groupOrdinal);
                if (group == null)
                {
                    group = new RelationalRelationshipReadGroup
                    {
                        GroupOrdinal = groupOrdinal,
                        GroupKind = reader.GetString(1),
                        IsCategory = reader.GetInt32(2) != 0
                    };
                    groups.Add(group);
                }

                if (!reader.IsDBNull(4) && Guid.TryParse(reader.GetString(4), out var updateId))
                {
                    group.Items.Add(new RelationalRelationshipItem
                    {
                        UpdateId = updateId,
                        RevisionNumber = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        ItemOrdinal = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
                    });
                }
            }

            return groups;
        }

        private string ReadRelationalTitle(int packageIndex)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = @"
SELECT value
FROM package_localized_elements
WHERE package_index = $packageIndex
  AND language = 'en'
  AND name = 'Title'
ORDER BY ordinal
LIMIT 1;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? null : result.ToString();
        }

        private string ReadRelationalPropertyElement(int packageIndex, string propertyName)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = @"
SELECT value
FROM package_property_elements
WHERE package_index = $packageIndex
  AND name = $propertyName
ORDER BY ordinal
LIMIT 1;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            command.Parameters.AddWithValue("$propertyName", propertyName);
            var result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? null : result.ToString();
        }

        private bool TryRelationalSimpleKeyLookup<T>(int packageIndex, string indexName, out T value)
        {
            object candidate = null;
            var found = false;

            if (string.Equals(
                    indexName,
                    Microsoft.PackageGraph.Storage.Index.AvailableIndexes.TitlesIndexName,
                    StringComparison.Ordinal))
            {
                candidate = ReadRelationalTitle(packageIndex);
                found = candidate != null;
            }
            else if (string.Equals(
                    indexName,
                    Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.CategoriesIndexName,
                    StringComparison.Ordinal))
            {
                var categories = ReadRelationalCategories(packageIndex);
                candidate = categories;
                found = categories.Count > 0;
            }
            else if (string.Equals(
                    indexName,
                    Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.KbArticleIndexName,
                    StringComparison.Ordinal))
            {
                candidate = ReadRelationalPropertyElement(packageIndex, "KBArticleID");
                found = candidate != null;
            }

            if (found && candidate is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        private bool TryRelationalListKeyLookup<T>(int packageIndex, string indexName, out List<T> value)
        {
            object candidate = null;

            if (string.Equals(
                    indexName,
                    Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.DriverMetadataIndexName,
                    StringComparison.Ordinal))
            {
                candidate = ReadRelationalDriverMetadata(packageIndex);
            }
            else if (string.Equals(
                    indexName,
                    Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.PrerequisitesIndexName,
                    StringComparison.Ordinal))
            {
                candidate = ReadRelationalPrerequisites(packageIndex);
            }
            else if (string.Equals(
                    indexName,
                    Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.FilesIndexName,
                    StringComparison.Ordinal))
            {
                candidate = ReadRelationalFiles(packageIndex);
            }
            else if (string.Equals(
                    indexName,
                    Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.IsSupersedingIndexName,
                    StringComparison.Ordinal))
            {
                candidate = ReadRelationalSupersededUpdates(packageIndex);
            }
            else if (string.Equals(
                    indexName,
                    Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.IsBundleIndexName,
                    StringComparison.Ordinal))
            {
                candidate = ReadRelationalBundledUpdates(packageIndex);
            }

            if (candidate is IEnumerable<T> sequence)
            {
                value = sequence.ToList();
                return value.Count > 0;
            }

            value = null;
            return false;
        }

        private bool TryRelationalPackageListLookupByCustomKey<T>(
            T key,
            string indexName,
            out List<IPackageIdentity> value)
        {
            value = new List<IPackageIdentity>();
            using var command = Connection.CreateCommand();

            if (string.Equals(
                    indexName,
                    Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.IsSupersededIndexName,
                    StringComparison.Ordinal) &&
                key is Guid supersededUpdateId)
            {
                command.CommandText = @"
SELECT DISTINCT relationship.package_index
FROM package_relationship_items AS relationship
JOIN packages AS package
  ON package.package_index = relationship.package_index
WHERE relationship.relationship_type = $relationshipType
  AND relationship.update_id = $updateId
ORDER BY relationship.package_index;";
                command.Parameters.AddWithValue(
                    "$relationshipType",
                    RelationalMetadataExtractor.SupersededRelationship);
                command.Parameters.AddWithValue("$updateId", supersededUpdateId.ToString("D"));
            }
            else if (string.Equals(
                         indexName,
                         Microsoft.PackageGraph.MicrosoftUpdate.Index.AvailableIndexes.BundledWithIndexName,
                         StringComparison.Ordinal) &&
                     key is MicrosoftUpdatePackageIdentity bundledIdentity)
            {
                command.CommandText = @"
SELECT DISTINCT relationship.package_index
FROM package_relationship_items AS relationship
JOIN packages AS package
  ON package.package_index = relationship.package_index
WHERE relationship.relationship_type = $relationshipType
  AND relationship.update_id = $updateId
  AND relationship.revision_number = $revisionNumber
ORDER BY relationship.package_index;";
                command.Parameters.AddWithValue(
                    "$relationshipType",
                    RelationalMetadataExtractor.BundledRelationship);
                command.Parameters.AddWithValue("$updateId", bundledIdentity.ID.ToString("D"));
                command.Parameters.AddWithValue("$revisionNumber", bundledIdentity.Revision);
            }
            else
            {
                value = null;
                return false;
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (_IndexToIdentityMap.TryGetValue(reader.GetInt32(0), out var identity))
                {
                    value.Add(identity);
                }
            }

            return value.Count > 0;
        }

        private List<IndexDefinition> GetRelationalIndexDefinitions()
        {
            return new List<IndexDefinition>
            {
                TitlesIndex.TitlesIndexDefinition,
                MicrosoftUpdatePartitionRegistration.DriverMetadata,
                MicrosoftUpdatePartitionRegistration.KbArticle,
                MicrosoftUpdatePartitionRegistration.IsSuperseded,
                MicrosoftUpdatePartitionRegistration.IsSuperseding,
                MicrosoftUpdatePartitionRegistration.IsBundle,
                MicrosoftUpdatePartitionRegistration.BundledWith,
                MicrosoftUpdatePartitionRegistration.Prerequisites,
                MicrosoftUpdatePartitionRegistration.Categories,
                MicrosoftUpdatePartitionRegistration.Files
            };
        }
private sealed class RelationalFileReadRow
        {
            public int Ordinal { get; set; }
            public string Sha1Base64 { get; set; }
            public string FileName { get; set; }
            public ulong Size { get; set; }
            public DateTime Modified { get; set; }
            public string PatchingType { get; set; }
            public string MuUrl { get; set; }
            public string UssUrl { get; set; }
            public List<ContentFileDigest> Digests { get; } = new();
        }

        private sealed class RelationalDriverReadRow
        {
            public long Id { get; set; }
            public string HardwareId { get; set; }
            public string WhqlDriverId { get; set; }
            public string Manufacturer { get; set; }
            public string Company { get; set; }
            public string Provider { get; set; }
            public DateTime DriverDate { get; set; }
            public ulong DriverVersion { get; set; }
            public string DriverClass { get; set; }
            public List<DriverFeatureScore> FeatureScores { get; } = new();
            public List<Guid> DistributionHardwareIds { get; } = new();
            public List<Guid> TargetHardwareIds { get; } = new();
        }

        private sealed class RelationalRelationshipReadGroup
        {
            public int GroupOrdinal { get; set; }
            public string GroupKind { get; set; }
            public bool IsCategory { get; set; }
            public List<RelationalRelationshipItem> Items { get; } = new();
        }
    }

    /// <summary>
    /// Canonical, normalized representation of the parts of Microsoft Update metadata
    /// that are required by the local package graph and by the client-sync endpoint.
    ///
    /// Arbitrary applicability expressions are deliberately kept as one protocol fragment:
    /// expanding the expression language into one SQLite row per XML node is larger than the
    /// original expression and provides no useful query surface. Driver metadata is removed
    /// from that fragment and stored relationally, then injected again when metadata is rendered.
    /// </summary>
    internal sealed class RelationalPackageRecord
    {
        public Guid UpdateId { get; set; }
        public int Revision { get; set; }
        public List<RelationalNameValue> PropertyAttributes { get; } = new();
        public List<RelationalElementValue> PropertyElements { get; } = new();
        public List<RelationalLocalizedElement> LocalizedElements { get; } = new();
        public List<RelationalRelationshipGroup> RelationshipGroups { get; } = new();
        public List<RelationalRelationshipItem> RelationshipItems { get; } = new();
        public List<string> RelationshipExtraElements { get; } = new();
        public string ApplicabilityTemplateXml { get; set; }
        public string HandlerSpecificXml { get; set; }
        public List<DriverMetadata> DriverMetadata { get; } = new();
        public List<UpdateFile> Files { get; } = new();
    }

    internal sealed class RelationalNameValue
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    internal class RelationalElementValue
    {
        public int Ordinal { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public string Xml { get; set; }
    }

    internal sealed class RelationalLocalizedElement : RelationalElementValue
    {
        public string Language { get; set; }
    }

    internal sealed class RelationalRelationshipGroup
    {
        public string RelationshipType { get; set; }
        public int GroupOrdinal { get; set; }
        public string GroupKind { get; set; }
        public bool IsCategory { get; set; }
    }

    internal sealed class RelationalRelationshipItem
    {
        public string RelationshipType { get; set; }
        public int GroupOrdinal { get; set; }
        public int ItemOrdinal { get; set; }
        public Guid UpdateId { get; set; }
        public int? RevisionNumber { get; set; }
    }

    internal static class RelationalMetadataExtractor
    {
        internal const string PrerequisitesRelationship = "Prerequisites";
        internal const string SupersededRelationship = "SupersededUpdates";
        internal const string BundledRelationship = "BundledUpdates";
        internal const string DirectGroup = "direct";
        internal const string AtLeastOneGroup = "at-least-one";

        public static RelationalPackageRecord Extract(MicrosoftUpdatePackage package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            using var metadataStream = package.GetMetadataStream()
                ?? throw new InvalidDataException($"Package {package.Id} does not expose metadata");
            var document = XDocument.Load(metadataStream, LoadOptions.PreserveWhitespace);
            var result = ExtractDocument(package.Id, document);

            // Parse driver metadata from the XDocument we already loaded. Calling
            // DriverUpdate.GetDriverMetadata() here would parse the same XML a second time for
            // the very large driver catalogue.
            ExtractDriverMetadata(document.Root, result);

            if (package.Files != null)
            {
                result.Files.AddRange(package.Files.OfType<UpdateFile>());
            }

            return result;
        }

        private static RelationalPackageRecord ExtractDocument(
            MicrosoftUpdatePackageIdentity identity,
            XDocument document)
        {
            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "Update", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Package {identity} has invalid Microsoft Update metadata");
            }

            var result = new RelationalPackageRecord
            {
                UpdateId = identity.ID,
                Revision = identity.Revision
            };

            ExtractProperties(root, result);
            ExtractLocalizedProperties(root, result);
            ExtractRelationships(root, result);
            ExtractApplicability(root, result);
            ExtractHandlerSpecificData(root, result);
            return result;
        }

        private static void ExtractDriverMetadata(XElement root, RelationalPackageRecord result)
        {
            var metadataNodes = root?
                .Descendants()
                .Where(element => element.Name.LocalName == "WindowsDriverMetaData")
                .ToList();
            if (metadataNodes == null || metadataNodes.Count == 0)
            {
                return;
            }

            foreach (var node in metadataNodes)
            {
                var date = DateTime.MinValue;
                var dateValue = AttributeValue(node, "DriverVerDate");
                if (!string.IsNullOrWhiteSpace(dateValue))
                {
                    DateTime.TryParseExact(
                        dateValue,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out date);
                }

                ulong version = 0;
                var versionValue = AttributeValue(node, "DriverVerVersion");
                if (!string.IsNullOrWhiteSpace(versionValue))
                {
                    try
                    {
                        version = DriverVersion.ParseVersionFromString(versionValue);
                    }
                    catch (Exception)
                    {
                        version = 0;
                    }
                }

                var featureScores = new List<DriverFeatureScore>();
                foreach (var featureScore in node.Elements()
                    .Where(element => element.Name.LocalName == "FeatureScore"))
                {
                    var scoreValue = AttributeValue(featureScore, "FeatureScore");
                    if (!byte.TryParse(
                            scoreValue,
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var score))
                    {
                        continue;
                    }

                    featureScores.Add(new DriverFeatureScore
                    {
                        OperatingSystem = AttributeValue(featureScore, "OperatingSystem"),
                        Score = score
                    });
                }

                var distributionIds = ParseHardwareIds(node, "DistributionComputerHardwareId");
                var targetIds = ParseHardwareIds(node, "TargetComputerHardwareId");
                result.DriverMetadata.Add(new DriverMetadata(
                    AttributeValue(node, "HardwareID"),
                    AttributeValue(node, "WhqlDriverID"),
                    AttributeValue(node, "Manufacturer"),
                    AttributeValue(node, "Company"),
                    AttributeValue(node, "Provider"),
                    date,
                    version,
                    AttributeValue(node, "Class"),
                    featureScores,
                    distributionIds,
                    targetIds));
            }
        }

        private static void ExtractProperties(XElement root, RelationalPackageRecord result)
        {
            var properties = DirectChild(root, "Properties");
            if (properties == null)
            {
                throw new InvalidDataException("Microsoft Update metadata has no Properties element");
            }

            foreach (var attribute in properties.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                result.PropertyAttributes.Add(new RelationalNameValue
                {
                    Name = attribute.Name.LocalName,
                    Value = attribute.Value
                });
            }

            var ordinal = 0;
            foreach (var child in properties.Elements())
            {
                result.PropertyElements.Add(ToElementValue(child, ordinal++));
            }
        }

        private static void ExtractLocalizedProperties(XElement root, RelationalPackageRecord result)
        {
            var collection = DirectChild(root, "LocalizedPropertiesCollection");
            if (collection == null)
            {
                return;
            }

            var candidates = collection.Elements()
                .Where(element => element.Name.LocalName == "LocalizedProperties")
                .Select(element => new
                {
                    Element = element,
                    Language = element.Elements()
                        .FirstOrDefault(child => child.Name.LocalName == "Language")?
                        .Value?
                        .Trim()
                })
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Language))
                .ToList();

            // Persist one English payload only. Do not relabel a non-English locale as English:
            // that would save space but return semantically incorrect metadata to Windows clients.
            var selected = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Language, "en", StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Language, "en-us", StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Language, "en-gb", StringComparison.OrdinalIgnoreCase));

            if (selected == null)
            {
                return;
            }

            var ordinal = 0;
            foreach (var child in selected.Element.Elements().Where(element => element.Name.LocalName != "Language"))
            {
                var value = ToElementValue(child, ordinal++);
                result.LocalizedElements.Add(new RelationalLocalizedElement
                {
                    Language = "en",
                    Ordinal = value.Ordinal,
                    Name = value.Name,
                    Value = value.Value,
                    Xml = value.Xml
                });
            }
        }

        private static void ExtractRelationships(XElement root, RelationalPackageRecord result)
        {
            var relationships = DirectChild(root, "Relationships");
            if (relationships == null)
            {
                return;
            }

            foreach (var relation in relationships.Elements())
            {
                switch (relation.Name.LocalName)
                {
                    case PrerequisitesRelationship:
                        ExtractGroupedRelationship(relation, PrerequisitesRelationship, result, true);
                        break;

                    case SupersededRelationship:
                        ExtractFlatRelationship(relation, SupersededRelationship, result);
                        break;

                    case BundledRelationship:
                        ExtractGroupedRelationship(relation, BundledRelationship, result, false);
                        break;

                    default:
                        result.RelationshipExtraElements.Add(SerializeElement(relation));
                        break;
                }
            }
        }

        private static void ExtractFlatRelationship(
            XElement relation,
            string relationshipType,
            RelationalPackageRecord result)
        {
            var groupOrdinal = 0;
            result.RelationshipGroups.Add(new RelationalRelationshipGroup
            {
                RelationshipType = relationshipType,
                GroupOrdinal = groupOrdinal,
                GroupKind = DirectGroup,
                IsCategory = false
            });

            var itemOrdinal = 0;
            foreach (var identityElement in relation.Elements().Where(element => element.Name.LocalName == "UpdateIdentity"))
            {
                if (TryReadIdentity(identityElement, out var updateId, out var revision))
                {
                    result.RelationshipItems.Add(new RelationalRelationshipItem
                    {
                        RelationshipType = relationshipType,
                        GroupOrdinal = groupOrdinal,
                        ItemOrdinal = itemOrdinal++,
                        UpdateId = updateId,
                        RevisionNumber = revision
                    });
                }
            }
        }

        private static void ExtractGroupedRelationship(
            XElement relation,
            string relationshipType,
            RelationalPackageRecord result,
            bool directItemsAreSeparateGroups)
        {
            var groupOrdinal = 0;

            var directIdentities = relation.Elements()
                .Where(element => element.Name.LocalName == "UpdateIdentity")
                .ToList();

            if (directItemsAreSeparateGroups)
            {
                foreach (var identityElement in directIdentities)
                {
                    if (!TryReadIdentity(identityElement, out var updateId, out var revision))
                    {
                        continue;
                    }

                    result.RelationshipGroups.Add(new RelationalRelationshipGroup
                    {
                        RelationshipType = relationshipType,
                        GroupOrdinal = groupOrdinal,
                        GroupKind = DirectGroup,
                        IsCategory = false
                    });
                    result.RelationshipItems.Add(new RelationalRelationshipItem
                    {
                        RelationshipType = relationshipType,
                        GroupOrdinal = groupOrdinal,
                        ItemOrdinal = 0,
                        UpdateId = updateId,
                        RevisionNumber = revision
                    });
                    groupOrdinal++;
                }
            }
            else if (directIdentities.Count > 0)
            {
                result.RelationshipGroups.Add(new RelationalRelationshipGroup
                {
                    RelationshipType = relationshipType,
                    GroupOrdinal = groupOrdinal,
                    GroupKind = DirectGroup,
                    IsCategory = false
                });

                var itemOrdinal = 0;
                foreach (var identityElement in directIdentities)
                {
                    if (TryReadIdentity(identityElement, out var updateId, out var revision))
                    {
                        result.RelationshipItems.Add(new RelationalRelationshipItem
                        {
                            RelationshipType = relationshipType,
                            GroupOrdinal = groupOrdinal,
                            ItemOrdinal = itemOrdinal++,
                            UpdateId = updateId,
                            RevisionNumber = revision
                        });
                    }
                }

                groupOrdinal++;
            }

            foreach (var atLeastOne in relation.Elements().Where(element => element.Name.LocalName == "AtLeastOne"))
            {
                var isCategory = string.Equals(
                    atLeastOne.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "IsCategory")?.Value,
                    "true",
                    StringComparison.OrdinalIgnoreCase);

                result.RelationshipGroups.Add(new RelationalRelationshipGroup
                {
                    RelationshipType = relationshipType,
                    GroupOrdinal = groupOrdinal,
                    GroupKind = AtLeastOneGroup,
                    IsCategory = isCategory
                });

                var itemOrdinal = 0;
                foreach (var identityElement in atLeastOne.Elements().Where(element => element.Name.LocalName == "UpdateIdentity"))
                {
                    if (TryReadIdentity(identityElement, out var updateId, out var revision))
                    {
                        result.RelationshipItems.Add(new RelationalRelationshipItem
                        {
                            RelationshipType = relationshipType,
                            GroupOrdinal = groupOrdinal,
                            ItemOrdinal = itemOrdinal++,
                            UpdateId = updateId,
                            RevisionNumber = revision
                        });
                    }
                }

                groupOrdinal++;
            }
        }

        private static bool TryReadIdentity(XElement element, out Guid updateId, out int? revision)
        {
            updateId = Guid.Empty;
            revision = null;

            var updateIdValue = element.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "UpdateID")?
                .Value;
            if (!Guid.TryParse(updateIdValue, out updateId))
            {
                return false;
            }

            var revisionValue = element.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "RevisionNumber")?
                .Value;
            if (int.TryParse(revisionValue, out var parsedRevision))
            {
                revision = parsedRevision;
            }

            return true;
        }

        private static void ExtractApplicability(XElement root, RelationalPackageRecord result)
        {
            var applicability = DirectChild(root, "ApplicabilityRules");
            if (applicability == null)
            {
                return;
            }

            var applicabilityTemplate = new XElement(applicability);
            var driverMetadataNodes = applicabilityTemplate
                .Descendants()
                .Where(element => element.Name.LocalName == "WindowsDriverMetaData")
                .ToList();
            foreach (var driverMetadataNode in driverMetadataNodes)
            {
                driverMetadataNode.Remove();
            }

            result.ApplicabilityTemplateXml = SerializeElement(applicabilityTemplate);
        }

        private static void ExtractHandlerSpecificData(XElement root, RelationalPackageRecord result)
        {
            var handler = DirectChild(root, "HandlerSpecificData");
            result.HandlerSpecificXml = handler == null ? null : SerializeElement(handler);
        }

        private static RelationalElementValue ToElementValue(XElement element, int ordinal)
        {
            var isSimple = !element.HasAttributes && !element.Elements().Any();
            return new RelationalElementValue
            {
                Ordinal = ordinal,
                Name = element.Name.LocalName,
                Value = isSimple ? element.Value : null,
                Xml = isSimple ? null : SerializeElement(element)
            };
        }

        private static string SerializeElement(XElement element)
        {
            var normalized = new XElement(element);
            foreach (var whitespace in normalized
                .DescendantNodes()
                .OfType<XText>()
                .Where(text => string.IsNullOrWhiteSpace(text.Value))
                .ToList())
            {
                whitespace.Remove();
            }

            return normalized.ToString(SaveOptions.DisableFormatting);
        }

        private static XElement DirectChild(XElement parent, string localName)
        {
            return parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);
        }

        private static List<Guid> ParseHardwareIds(XElement node, string localName)
        {
            var result = new List<Guid>();
            foreach (var element in node.Elements().Where(element => element.Name.LocalName == localName))
            {
                if (Guid.TryParse(element.Value, out var hardwareId))
                {
                    result.Add(hardwareId);
                }
            }

            return result;
        }

        private static string AttributeValue(XElement element, string localName)
        {
            return element.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == localName)?
                .Value;
        }
    }
}
