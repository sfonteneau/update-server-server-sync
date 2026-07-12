// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate.Index;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Drivers;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
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
    /// This replaces the historical local store layout that created append-only
    /// delta archives named 0.zip, 1.zip, ... . Metadata, file metadata and the
    /// serialized index container now live in store/metadata.sqlite.
    /// </summary>
    class SQLitePackageStore : IMetadataSink, IMetadataStore, IMetadataLookup, IMicrosoftUpdateFileLocationLookup, ISyncAnchorStore, ISyncCheckpointStore, IObservedInventoryStore, IDriverSyncStateStore, IReloadableMetadataStore, IMetadataCatalogPublicationControl, IObservedOperationsStore
    {
        public const string DatabaseFileName = "metadata.sqlite";

        private const int SchemaVersion = 8;
        private const string IndexBlobKey = "indexes.zip";
        private const string CompressionNone = "none";
        private const string CompressionBrotli = "br";
        private const string CompressionGZip = "gzip";
        private const int OptimizeBatchSize = 200;
        private const int MaxObservedValuesPerType = 20000;
        private const int MaxObservedIdentifierLength = 2048;
        private const string CatalogPublicationDeferredKey = "catalog_publication_deferred";
        private const string CatalogUnpublishedChangesKey = "catalog_unpublished_changes";

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
        private bool CatalogGenerationDirty;
        private bool IsCatalogGenerationPublicationDeferred;
        private MetadataStoreGenerationInfo LoadedMetadataGeneration;

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
            // Client scans and the scheduled fetch-observed process can write to
            // the same WAL database. Wait for the current writer instead of
            // failing a scan immediately with SQLITE_BUSY.
            ExecuteNonQuery("PRAGMA busy_timeout=30000;");

            if (isNewDatabase)
            {
                InitializeSchema();
            }
            else
            {
                ValidateExistingSchema();
            }

            IsCatalogGenerationPublicationDeferred =
                string.Equals(ReadProperty(CatalogPublicationDeferredKey), "1", StringComparison.Ordinal);
            CatalogGenerationDirty =
                string.Equals(ReadProperty(CatalogUnpublishedChangesKey), "1", StringComparison.Ordinal);
            LoadPackageMaps();
            LoadIndexes();
            LoadedMetadataGeneration = ReadMetadataGenerationInfo();

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
    published INTEGER NOT NULL DEFAULT 1 CHECK(published IN (0, 1)),
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

CREATE INDEX IF NOT EXISTS idx_packages_published_type
ON packages(published, package_type, package_index);

CREATE TABLE IF NOT EXISTS client_sync_packages (
    package_index INTEGER PRIMARY KEY,
    update_id TEXT NOT NULL,
    revision_number INTEGER NOT NULL,
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_client_sync_packages_update
ON client_sync_packages(update_id, revision_number, package_index);

CREATE TABLE IF NOT EXISTS client_sync_prerequisites (
    package_index INTEGER NOT NULL,
    prerequisite_update_id TEXT NOT NULL,
    is_category INTEGER NOT NULL DEFAULT 0 CHECK(is_category IN (0, 1)),
    PRIMARY KEY(package_index, prerequisite_update_id, is_category),
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_client_sync_prerequisites_target
ON client_sync_prerequisites(prerequisite_update_id, package_index);

CREATE TABLE IF NOT EXISTS client_sync_supersedence (
    superseding_package_index INTEGER NOT NULL,
    superseded_update_id TEXT NOT NULL,
    PRIMARY KEY(superseding_package_index, superseded_update_id),
    FOREIGN KEY(superseding_package_index) REFERENCES packages(package_index) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_client_sync_supersedence_target
ON client_sync_supersedence(superseded_update_id, superseding_package_index);

CREATE TABLE IF NOT EXISTS client_sync_bundles (
    bundle_package_index INTEGER NOT NULL,
    bundled_update_id TEXT NOT NULL,
    bundled_revision_number INTEGER NOT NULL,
    PRIMARY KEY(bundle_package_index, bundled_update_id, bundled_revision_number),
    FOREIGN KEY(bundle_package_index) REFERENCES packages(package_index) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_client_sync_bundles_target
ON client_sync_bundles(bundled_update_id, bundled_revision_number, bundle_package_index);

CREATE TABLE IF NOT EXISTS client_sync_driver_hardware_ids (
    package_index INTEGER NOT NULL,
    metadata_ordinal INTEGER NOT NULL,
    hardware_id TEXT NOT NULL,
    PRIMARY KEY(package_index, metadata_ordinal),
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_client_sync_driver_hardware_id
ON client_sync_driver_hardware_ids(hardware_id, package_index, metadata_ordinal);

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

CREATE TABLE IF NOT EXISTS driver_sync_identifier_anchors (
    scope_key TEXT NOT NULL,
    identifier_type INTEGER NOT NULL,
    identifier TEXT NOT NULL,
    anchor TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(scope_key, identifier_type, identifier)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_driver_sync_identifier_anchors_scope_anchor
ON driver_sync_identifier_anchors(scope_key, anchor);

CREATE TABLE IF NOT EXISTS driver_sync_checkpoint_scopes (
    anchor_key TEXT PRIMARY KEY,
    scope_key TEXT NOT NULL,
    FOREIGN KEY(anchor_key) REFERENCES sync_checkpoints(anchor_key) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_driver_sync_checkpoint_scopes_scope
ON driver_sync_checkpoint_scopes(scope_key);

CREATE TABLE IF NOT EXISTS driver_sync_checkpoint_members (
    anchor_key TEXT NOT NULL,
    identifier_type INTEGER NOT NULL,
    identifier TEXT NOT NULL,
    PRIMARY KEY(anchor_key, identifier_type, identifier),
    FOREIGN KEY(anchor_key) REFERENCES driver_sync_checkpoint_scopes(anchor_key) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS observed_detectoids (
    update_id TEXT NOT NULL,
    revision_number INTEGER NOT NULL,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    observation_count INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY(update_id, revision_number)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_observed_detectoids_last_seen
ON observed_detectoids(last_seen);

CREATE TABLE IF NOT EXISTS observed_pnp_hardware_ids (
    hardware_id TEXT PRIMARY KEY,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    observation_count INTEGER NOT NULL DEFAULT 1
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_observed_pnp_hardware_ids_last_seen
ON observed_pnp_hardware_ids(last_seen);

CREATE TABLE IF NOT EXISTS observed_compatible_ids (
    compatible_id TEXT PRIMARY KEY,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    observation_count INTEGER NOT NULL DEFAULT 1
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_observed_compatible_ids_last_seen
ON observed_compatible_ids(last_seen);

CREATE TABLE IF NOT EXISTS observed_computer_ids (
    computer_id TEXT PRIMARY KEY,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    observation_count INTEGER NOT NULL DEFAULT 1
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_observed_computer_ids_last_seen
ON observed_computer_ids(last_seen);

CREATE TABLE IF NOT EXISTS detectoid_product_map (
    detectoid_update_id TEXT NOT NULL,
    product_category_id TEXT NOT NULL,
    source_flags INTEGER NOT NULL,
    PRIMARY KEY(detectoid_update_id, product_category_id)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_detectoid_product_map_product
ON detectoid_product_map(product_category_id);

CREATE TABLE IF NOT EXISTS observed_fetch_runs (
    run_id INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at TEXT NOT NULL,
    completed_at TEXT NULL,
    status INTEGER NOT NULL,
    seen_within_days INTEGER NOT NULL,
    include_products INTEGER NOT NULL CHECK(include_products IN (0, 1)),
    include_drivers INTEGER NOT NULL CHECK(include_drivers IN (0, 1)),
    include_compatible_ids INTEGER NOT NULL CHECK(include_compatible_ids IN (0, 1)),
    dry_run INTEGER NOT NULL CHECK(dry_run IN (0, 1)),
    package_count_before INTEGER NOT NULL,
    package_count_after INTEGER NULL,
    summary TEXT NULL,
    error TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_observed_fetch_runs_started_at
ON observed_fetch_runs(started_at DESC);

INSERT OR IGNORE INTO store_properties(key, value)
VALUES ('catalog_generation', '0');

INSERT OR IGNORE INTO store_properties(key, value)
VALUES ('catalog_last_changed', '1970-01-01T00:00:00.0000000+00:00');

INSERT OR IGNORE INTO store_properties(key, value)
VALUES ('catalog_publication_deferred', '0');

INSERT OR IGNORE INTO store_properties(key, value)
VALUES ('catalog_unpublished_changes', '0');
");

            using var command = Connection.CreateCommand();
            command.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ('schema_version', $schemaVersion)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$schemaVersion", SchemaVersion.ToString());
            command.ExecuteNonQuery();
        }

        private void ValidateExistingSchema()
        {
            using (var tableCommand = Connection.CreateCommand())
            {
                tableCommand.CommandText = @"
SELECT COUNT(*)
FROM sqlite_schema
WHERE type = 'table'
  AND name = 'store_properties';";
                if (Convert.ToInt32(
                        tableCommand.ExecuteScalar(),
                        CultureInfo.InvariantCulture) != 1)
                {
                    throw new InvalidDataException(
                        "Unsupported SQLite metadata store. Delete metadata.sqlite and run pre-fetch again.");
                }
            }

            var schemaVersion = ReadProperty("schema_version");
            if (!string.Equals(
                    schemaVersion,
                    SchemaVersion.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unsupported SQLite metadata schema '{schemaVersion ?? "missing"}'. " +
                    $"Delete metadata.sqlite and run pre-fetch again with schema {SchemaVersion}.");
            }

            var requiredTables = new[]
            {
                "packages",
                "client_sync_packages",
                "client_sync_prerequisites",
                "client_sync_supersedence",
                "client_sync_bundles",
                "client_sync_driver_hardware_ids",
                "file_locations",
                "package_file_map",
                "observed_detectoids",
                "observed_pnp_hardware_ids",
                "observed_compatible_ids",
                "observed_computer_ids"
            };

            using (var command = Connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_schema
WHERE type = 'table'
  AND name = $tableName;";
                var parameter = command.Parameters.Add("$tableName", SqliteType.Text);
                command.Prepare();
                foreach (var tableName in requiredTables)
                {
                    parameter.Value = tableName;
                    if (Convert.ToInt32(
                            command.ExecuteScalar(),
                            CultureInfo.InvariantCulture) != 1)
                    {
                        throw new InvalidDataException(
                            $"SQLite metadata schema {SchemaVersion} is incomplete: " +
                            $"missing table '{tableName}'. Delete metadata.sqlite and run pre-fetch again.");
                    }
                }
            }

            if (!ColumnExists("packages", "published"))
            {
                throw new InvalidDataException(
                    "SQLite metadata schema is missing packages.published. " +
                    "Delete metadata.sqlite and run pre-fetch again.");
            }

            _FileLocationFileJsonIsRequired = false;
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

        private MetadataStoreGenerationInfo ReadMetadataGenerationInfo()
        {
            long generation = 0;
            var generationValue = ReadProperty("catalog_generation");
            if (!string.IsNullOrWhiteSpace(generationValue))
            {
                long.TryParse(
                    generationValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out generation);
            }

            var lastChanged = DateTimeOffset.MinValue;
            var lastChangedValue = ReadProperty("catalog_last_changed");
            if (!string.IsNullOrWhiteSpace(lastChangedValue)
                && DateTimeOffset.TryParse(
                    lastChangedValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedLastChanged))
            {
                lastChanged = parsedLastChanged.ToUniversalTime();
            }

            return new MetadataStoreGenerationInfo(
                generation,
                lastChanged,
                string.Equals(
                    ReadProperty(CatalogPublicationDeferredKey),
                    "1",
                    StringComparison.Ordinal),
                string.Equals(
                    ReadProperty(CatalogUnpublishedChangesKey),
                    "1",
                    StringComparison.Ordinal));
        }

        private MetadataStoreGenerationInfo PublishCatalogGeneration()
        {
            var changedAt = DateTimeOffset.UtcNow;
            var changedAtText = changedAt.ToString("O", CultureInfo.InvariantCulture);

            using var transaction = Connection.BeginTransaction();
            using (var publishPackagesCommand = Connection.CreateCommand())
            {
                publishPackagesCommand.Transaction = transaction;
                publishPackagesCommand.CommandText = @"
UPDATE packages
SET published = 1
WHERE published = 0;";
                publishPackagesCommand.ExecuteNonQuery();
            }

            using (var generationCommand = Connection.CreateCommand())
            {
                generationCommand.Transaction = transaction;
                generationCommand.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ('catalog_generation', '1')
ON CONFLICT(key) DO UPDATE SET
    value = CAST(CAST(store_properties.value AS INTEGER) + 1 AS TEXT);";
                generationCommand.ExecuteNonQuery();
            }

            using (var timestampCommand = Connection.CreateCommand())
            {
                timestampCommand.Transaction = transaction;
                timestampCommand.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ('catalog_last_changed', $changedAt)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                timestampCommand.Parameters.AddWithValue("$changedAt", changedAtText);
                timestampCommand.ExecuteNonQuery();
            }

            using (var publicationStateCommand = Connection.CreateCommand())
            {
                publicationStateCommand.Transaction = transaction;
                publicationStateCommand.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ($deferredKey, '0'), ($unpublishedKey, '0')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                publicationStateCommand.Parameters.AddWithValue(
                    "$deferredKey",
                    CatalogPublicationDeferredKey);
                publicationStateCommand.Parameters.AddWithValue(
                    "$unpublishedKey",
                    CatalogUnpublishedChangesKey);
                publicationStateCommand.ExecuteNonQuery();
            }

            long generation;
            using (var readCommand = Connection.CreateCommand())
            {
                readCommand.Transaction = transaction;
                readCommand.CommandText = @"
SELECT CAST(value AS INTEGER)
FROM store_properties
WHERE key = 'catalog_generation';";
                generation = Convert.ToInt64(
                    readCommand.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }

            transaction.Commit();
            IsCatalogGenerationPublicationDeferred = false;
            return new MetadataStoreGenerationInfo(
                generation,
                changedAt,
                publicationDeferred: false,
                hasUnpublishedChanges: false);
        }

        private void PersistUnpublishedCatalogChanges()
        {
            WriteProperty(CatalogUnpublishedChangesKey, "1");
        }

        private void ClearCatalogPublicationState()
        {
            using var transaction = Connection.BeginTransaction();
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ($deferredKey, '0'), ($unpublishedKey, '0')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$deferredKey", CatalogPublicationDeferredKey);
            command.Parameters.AddWithValue("$unpublishedKey", CatalogUnpublishedChangesKey);
            command.ExecuteNonQuery();
            transaction.Commit();
            IsCatalogGenerationPublicationDeferred = false;
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

        // Observed client inventory support. These operations are local-only and
        // never contact an upstream update service.
        public void RecordObservedInventory(ObservedInventoryBatch observations)
        {
            if (observations == null || observations.IsEmpty)
            {
                return;
            }

            var observedAt = observations.ObservedAt
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);

            var detectoids = observations.Detectoids
                .Where(value => value != null
                    && value.UpdateId != Guid.Empty
                    && value.RevisionNumber >= 0)
                .GroupBy(value => new { value.UpdateId, value.RevisionNumber })
                .Select(group => group.First())
                .Take(MaxObservedValuesPerType)
                .ToList();
            var pnpHardwareIds = NormalizeObservedIdentifiers(observations.PnpHardwareIds);
            var compatibleIds = NormalizeObservedIdentifiers(observations.CompatibleIds);
            var computerIds = observations.ComputerIds
                .Where(value => value != Guid.Empty)
                .Distinct()
                .Take(MaxObservedValuesPerType)
                .Select(value => value.ToString("D"))
                .ToList();

            if (detectoids.Count == 0
                && pnpHardwareIds.Count == 0
                && compatibleIds.Count == 0
                && computerIds.Count == 0)
            {
                return;
            }

            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                UpsertObservedDetectoids(transaction, detectoids, observedAt);
                UpsertObservedStringIdentifiers(
                    transaction,
                    "observed_pnp_hardware_ids",
                    "hardware_id",
                    pnpHardwareIds,
                    observedAt);
                UpsertObservedStringIdentifiers(
                    transaction,
                    "observed_compatible_ids",
                    "compatible_id",
                    compatibleIds,
                    observedAt);
                UpsertObservedStringIdentifiers(
                    transaction,
                    "observed_computer_ids",
                    "computer_id",
                    computerIds,
                    observedAt);
                transaction.Commit();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public IReadOnlyList<ObservedDetectoidObservation> GetObservedDetectoids(
            DateTimeOffset? seenSince = null)
        {
            StateLock.EnterReadLock();
            try
            {
                var result = new List<ObservedDetectoidObservation>();
                using var command = Connection.CreateCommand();
                command.CommandText = @"
SELECT update_id, revision_number, first_seen, last_seen, observation_count
FROM observed_detectoids
WHERE $seenSince IS NULL OR last_seen >= $seenSince
ORDER BY last_seen DESC, update_id, revision_number;";
                AddObservedSeenSinceParameter(command, seenSince);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!Guid.TryParse(reader.GetString(0), out var updateId))
                    {
                        continue;
                    }

                    result.Add(new ObservedDetectoidObservation(
                        updateId,
                        reader.GetInt32(1),
                        ParseCheckpointTimestamp(reader.GetString(2)),
                        ParseCheckpointTimestamp(reader.GetString(3)),
                        reader.GetInt64(4)));
                }

                return result;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public IReadOnlyList<ObservedIdentifierObservation> GetObservedPnpHardwareIds(
            DateTimeOffset? seenSince = null)
        {
            StateLock.EnterReadLock();
            try
            {
                return ReadObservedStringIdentifiers(
                    "observed_pnp_hardware_ids",
                    "hardware_id",
                    seenSince);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public IReadOnlyList<ObservedIdentifierObservation> GetObservedCompatibleIds(
            DateTimeOffset? seenSince = null)
        {
            StateLock.EnterReadLock();
            try
            {
                return ReadObservedStringIdentifiers(
                    "observed_compatible_ids",
                    "compatible_id",
                    seenSince);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public IReadOnlyList<ObservedComputerIdObservation> GetObservedComputerIds(
            DateTimeOffset? seenSince = null)
        {
            StateLock.EnterReadLock();
            try
            {
                var result = new List<ObservedComputerIdObservation>();
                using var command = Connection.CreateCommand();
                command.CommandText = @"
SELECT computer_id, first_seen, last_seen, observation_count
FROM observed_computer_ids
WHERE $seenSince IS NULL OR last_seen >= $seenSince
ORDER BY last_seen DESC, computer_id;";
                AddObservedSeenSinceParameter(command, seenSince);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!Guid.TryParse(reader.GetString(0), out var computerId))
                    {
                        continue;
                    }

                    result.Add(new ObservedComputerIdObservation(
                        computerId,
                        ParseCheckpointTimestamp(reader.GetString(1)),
                        ParseCheckpointTimestamp(reader.GetString(2)),
                        reader.GetInt64(3)));
                }

                return result;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public ObservedInventoryStatus GetObservedInventoryStatus(DateTimeOffset? seenSince = null)
        {
            var normalizedSeenSince = seenSince?.ToUniversalTime();

            StateLock.EnterReadLock();
            try
            {
                return new ObservedInventoryStatus(
                    normalizedSeenSince,
                    ReadObservedInventoryKindStatus("observed_detectoids", normalizedSeenSince),
                    ReadObservedInventoryKindStatus("observed_pnp_hardware_ids", normalizedSeenSince),
                    ReadObservedInventoryKindStatus("observed_compatible_ids", normalizedSeenSince),
                    ReadObservedInventoryKindStatus("observed_computer_ids", normalizedSeenSince));
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public void ReplaceDetectoidProductMappings(
            IEnumerable<DetectoidProductMapping> mappings,
            DateTimeOffset rebuiltAt,
            string algorithmVersion)
        {
            var normalizedMappings = (mappings ?? Array.Empty<DetectoidProductMapping>())
                .Where(mapping => mapping != null
                    && mapping.DetectoidUpdateId != Guid.Empty
                    && mapping.ProductCategoryId != Guid.Empty
                    && mapping.Source != DetectoidProductMappingSource.None)
                .GroupBy(mapping => new
                {
                    mapping.DetectoidUpdateId,
                    mapping.ProductCategoryId
                })
                .Select(group => new DetectoidProductMapping(
                    group.Key.DetectoidUpdateId,
                    group.Key.ProductCategoryId,
                    group.Aggregate(
                        DetectoidProductMappingSource.None,
                        (flags, mapping) => flags | mapping.Source)))
                .OrderBy(mapping => mapping.DetectoidUpdateId)
                .ThenBy(mapping => mapping.ProductCategoryId)
                .ToList();

            var normalizedRebuiltAt = rebuiltAt == default
                ? DateTimeOffset.UtcNow
                : rebuiltAt.ToUniversalTime();
            var rebuiltAtText = normalizedRebuiltAt.ToString("O", CultureInfo.InvariantCulture);
            var normalizedAlgorithmVersion = string.IsNullOrWhiteSpace(algorithmVersion)
                ? "unknown"
                : algorithmVersion.Trim();

            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();

                using (var cleanupCommand = Connection.CreateCommand())
                {
                    cleanupCommand.Transaction = transaction;
                    cleanupCommand.CommandText = @"
DELETE FROM observed_detectoids
WHERE NOT EXISTS (
    SELECT 1
    FROM packages AS package
    WHERE package.update_id = observed_detectoids.update_id
      AND package.package_type = $detectoidType
      AND package.published = 1
);";
                    cleanupCommand.Parameters.AddWithValue(
                        "$detectoidType",
                        (int)StoredPackageType.MicrosoftUpdateDetectoid);
                    cleanupCommand.ExecuteNonQuery();
                }

                using (var deleteCommand = Connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText = "DELETE FROM detectoid_product_map;";
                    deleteCommand.ExecuteNonQuery();
                }

                if (normalizedMappings.Count > 0)
                {
                    using var insertCommand = Connection.CreateCommand();
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = @"
INSERT INTO detectoid_product_map(
    detectoid_update_id,
    product_category_id,
    source_flags)
VALUES ($detectoidUpdateId, $productCategoryId, $sourceFlags);";
                    var detectoidParameter = insertCommand.Parameters.Add("$detectoidUpdateId", SqliteType.Text);
                    var productParameter = insertCommand.Parameters.Add("$productCategoryId", SqliteType.Text);
                    var sourceParameter = insertCommand.Parameters.Add("$sourceFlags", SqliteType.Integer);
                    insertCommand.Prepare();

                    foreach (var mapping in normalizedMappings)
                    {
                        detectoidParameter.Value = mapping.DetectoidUpdateId.ToString("D");
                        productParameter.Value = mapping.ProductCategoryId.ToString("D");
                        sourceParameter.Value = (int)mapping.Source;
                        insertCommand.ExecuteNonQuery();
                    }
                }

                using (var rebuiltAtCommand = Connection.CreateCommand())
                {
                    rebuiltAtCommand.Transaction = transaction;
                    rebuiltAtCommand.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ('detectoid_product_map_rebuilt_at', $rebuiltAt)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                    rebuiltAtCommand.Parameters.AddWithValue("$rebuiltAt", rebuiltAtText);
                    rebuiltAtCommand.ExecuteNonQuery();
                }

                using (var versionCommand = Connection.CreateCommand())
                {
                    versionCommand.Transaction = transaction;
                    versionCommand.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ('detectoid_product_map_algorithm_version', $algorithmVersion)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                    versionCommand.Parameters.AddWithValue("$algorithmVersion", normalizedAlgorithmVersion);
                    versionCommand.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public IReadOnlyList<ObservedProductCategory> GetObservedProductCategories(
            DateTimeOffset? seenSince = null)
        {
            StateLock.EnterReadLock();
            try
            {
                var result = new List<ObservedProductCategory>();
                using var command = Connection.CreateCommand();
                command.CommandText = @"
WITH active_detectoids AS (
    SELECT
        update_id,
        MAX(last_seen) AS last_seen,
        SUM(observation_count) AS observation_count
    FROM observed_detectoids
    WHERE $seenSince IS NULL OR last_seen >= $seenSince
    GROUP BY update_id
)
SELECT
    mapping.product_category_id,
    MAX(active.last_seen) AS last_seen,
    COUNT(DISTINCT active.update_id) AS detectoid_count,
    SUM(active.observation_count) AS observation_count
FROM active_detectoids AS active
JOIN detectoid_product_map AS mapping
  ON mapping.detectoid_update_id = active.update_id
GROUP BY mapping.product_category_id
ORDER BY last_seen DESC, mapping.product_category_id;";
                AddObservedSeenSinceParameter(command, seenSince);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!Guid.TryParse(reader.GetString(0), out var productCategoryId))
                    {
                        continue;
                    }

                    result.Add(new ObservedProductCategory(
                        productCategoryId,
                        ParseCheckpointTimestamp(reader.GetString(1)),
                        reader.GetInt64(2),
                        reader.GetInt64(3)));
                }

                return result;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public DetectoidProductMapStatus GetDetectoidProductMapStatus(
            DateTimeOffset? seenSince = null)
        {
            StateLock.EnterReadLock();
            try
            {
                long mappingCount;
                long mappedDetectoidCount;
                long productCount;
                using (var mappingCommand = Connection.CreateCommand())
                {
                    mappingCommand.CommandText = @"
SELECT
    COUNT(*),
    COUNT(DISTINCT detectoid_update_id),
    COUNT(DISTINCT product_category_id)
FROM detectoid_product_map;";
                    using var reader = mappingCommand.ExecuteReader();
                    reader.Read();
                    mappingCount = reader.GetInt64(0);
                    mappedDetectoidCount = reader.GetInt64(1);
                    productCount = reader.GetInt64(2);
                }

                long activeMappedDetectoidCount;
                long activeUnmappedDetectoidCount;
                using (var activeCommand = Connection.CreateCommand())
                {
                    activeCommand.CommandText = @"
WITH active_detectoids AS (
    SELECT DISTINCT update_id
    FROM observed_detectoids
    WHERE $seenSince IS NULL OR last_seen >= $seenSince
)
SELECT
    COALESCE(SUM(CASE WHEN EXISTS (
        SELECT 1
        FROM detectoid_product_map AS mapping
        WHERE mapping.detectoid_update_id = active.update_id
    ) THEN 1 ELSE 0 END), 0),
    COALESCE(SUM(CASE WHEN NOT EXISTS (
        SELECT 1
        FROM detectoid_product_map AS mapping
        WHERE mapping.detectoid_update_id = active.update_id
    ) THEN 1 ELSE 0 END), 0)
FROM active_detectoids AS active;";
                    AddObservedSeenSinceParameter(activeCommand, seenSince);
                    using var reader = activeCommand.ExecuteReader();
                    reader.Read();
                    activeMappedDetectoidCount = reader.GetInt64(0);
                    activeUnmappedDetectoidCount = reader.GetInt64(1);
                }

                DateTimeOffset? rebuiltAt = null;
                var rebuiltAtValue = ReadProperty("detectoid_product_map_rebuilt_at");
                if (!string.IsNullOrWhiteSpace(rebuiltAtValue))
                {
                    rebuiltAt = ParseCheckpointTimestamp(rebuiltAtValue);
                }

                var algorithmVersion = ReadProperty("detectoid_product_map_algorithm_version");

                return new DetectoidProductMapStatus(
                    mappingCount,
                    mappedDetectoidCount,
                    productCount,
                    activeMappedDetectoidCount,
                    activeUnmappedDetectoidCount,
                    rebuiltAt,
                    algorithmVersion);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public long PruneObservedInventory(DateTimeOffset olderThan)
        {
            var cutoff = olderThan
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);

            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                long deleted = 0;
                deleted += DeleteObservedRows(transaction, "observed_detectoids", cutoff);
                deleted += DeleteObservedRows(transaction, "observed_pnp_hardware_ids", cutoff);
                deleted += DeleteObservedRows(transaction, "observed_compatible_ids", cutoff);
                deleted += DeleteObservedRows(transaction, "observed_computer_ids", cutoff);
                transaction.Commit();
                TryReclaimCheckpointStorage();
                return deleted;
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public long StartObservedFetchRun(
            DateTimeOffset startedAt,
            int seenWithinDays,
            bool includeProducts,
            bool includeDrivers,
            bool includeCompatibleIds,
            bool dryRun,
            long packageCountBefore)
        {
            var normalizedStartedAt = startedAt == default
                ? DateTimeOffset.UtcNow
                : startedAt.ToUniversalTime();
            var startedAtText = normalizedStartedAt.ToString("O", CultureInfo.InvariantCulture);

            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();

                // A running row left by a terminated process can never complete.
                // Close it explicitly before creating the next scheduled run.
                using (var interruptedCommand = Connection.CreateCommand())
                {
                    interruptedCommand.Transaction = transaction;
                    interruptedCommand.CommandText = @"
UPDATE observed_fetch_runs
SET completed_at = $completedAt,
    status = $failedStatus,
    error = COALESCE(error, 'The previous fetch-observed process ended before recording completion.')
WHERE status = $runningStatus
  AND completed_at IS NULL;";
                    interruptedCommand.Parameters.AddWithValue("$completedAt", startedAtText);
                    interruptedCommand.Parameters.AddWithValue("$failedStatus", (int)ObservedFetchRunStatus.Failed);
                    interruptedCommand.Parameters.AddWithValue("$runningStatus", (int)ObservedFetchRunStatus.Running);
                    interruptedCommand.ExecuteNonQuery();
                }

                using (var insertCommand = Connection.CreateCommand())
                {
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = @"
INSERT INTO observed_fetch_runs(
    started_at,
    completed_at,
    status,
    seen_within_days,
    include_products,
    include_drivers,
    include_compatible_ids,
    dry_run,
    package_count_before,
    package_count_after,
    summary,
    error)
VALUES (
    $startedAt,
    NULL,
    $status,
    $seenWithinDays,
    $includeProducts,
    $includeDrivers,
    $includeCompatibleIds,
    $dryRun,
    $packageCountBefore,
    NULL,
    NULL,
    NULL);";
                    insertCommand.Parameters.AddWithValue("$startedAt", startedAtText);
                    insertCommand.Parameters.AddWithValue("$status", (int)ObservedFetchRunStatus.Running);
                    insertCommand.Parameters.AddWithValue("$seenWithinDays", Math.Max(0, seenWithinDays));
                    insertCommand.Parameters.AddWithValue("$includeProducts", includeProducts ? 1 : 0);
                    insertCommand.Parameters.AddWithValue("$includeDrivers", includeDrivers ? 1 : 0);
                    insertCommand.Parameters.AddWithValue("$includeCompatibleIds", includeCompatibleIds ? 1 : 0);
                    insertCommand.Parameters.AddWithValue("$dryRun", dryRun ? 1 : 0);
                    insertCommand.Parameters.AddWithValue("$packageCountBefore", Math.Max(0, packageCountBefore));
                    insertCommand.ExecuteNonQuery();
                }

                long runId;
                using (var idCommand = Connection.CreateCommand())
                {
                    idCommand.Transaction = transaction;
                    idCommand.CommandText = "SELECT last_insert_rowid();";
                    runId = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                using (var retentionCommand = Connection.CreateCommand())
                {
                    retentionCommand.Transaction = transaction;
                    retentionCommand.CommandText = @"
DELETE FROM observed_fetch_runs
WHERE run_id NOT IN (
    SELECT run_id
    FROM observed_fetch_runs
    ORDER BY run_id DESC
    LIMIT 100
);";
                    retentionCommand.ExecuteNonQuery();
                }

                transaction.Commit();
                return runId;
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public void CompleteObservedFetchRun(
            long runId,
            DateTimeOffset completedAt,
            ObservedFetchRunStatus status,
            long packageCountAfter,
            string summary,
            string error)
        {
            if (runId <= 0)
            {
                return;
            }

            var normalizedCompletedAt = completedAt == default
                ? DateTimeOffset.UtcNow
                : completedAt.ToUniversalTime();

            StateLock.EnterWriteLock();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = @"
UPDATE observed_fetch_runs
SET completed_at = $completedAt,
    status = $status,
    package_count_after = $packageCountAfter,
    summary = $summary,
    error = $error
WHERE run_id = $runId;";
                command.Parameters.AddWithValue("$runId", runId);
                command.Parameters.AddWithValue(
                    "$completedAt",
                    normalizedCompletedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$status", (int)status);
                command.Parameters.AddWithValue("$packageCountAfter", Math.Max(0, packageCountAfter));
                command.Parameters.AddWithValue(
                    "$summary",
                    string.IsNullOrWhiteSpace(summary) ? DBNull.Value : summary);
                command.Parameters.AddWithValue(
                    "$error",
                    string.IsNullOrWhiteSpace(error) ? DBNull.Value : error);
                command.ExecuteNonQuery();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public IReadOnlyList<ObservedFetchRunInfo> GetObservedFetchRuns(int maxCount)
        {
            var limit = Math.Max(0, Math.Min(maxCount, 100));
            if (limit == 0)
            {
                return Array.Empty<ObservedFetchRunInfo>();
            }

            StateLock.EnterReadLock();
            try
            {
                var result = new List<ObservedFetchRunInfo>();
                using var command = Connection.CreateCommand();
                command.CommandText = @"
SELECT
    run_id,
    started_at,
    completed_at,
    status,
    seen_within_days,
    include_products,
    include_drivers,
    include_compatible_ids,
    dry_run,
    package_count_before,
    package_count_after,
    summary,
    error
FROM observed_fetch_runs
ORDER BY run_id DESC
LIMIT $limit;";
                command.Parameters.AddWithValue("$limit", limit);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new ObservedFetchRunInfo(
                        reader.GetInt64(0),
                        ParseCheckpointTimestamp(reader.GetString(1)),
                        reader.IsDBNull(2)
                            ? null
                            : ParseCheckpointTimestamp(reader.GetString(2)),
                        Enum.IsDefined(typeof(ObservedFetchRunStatus), reader.GetInt32(3))
                            ? (ObservedFetchRunStatus)reader.GetInt32(3)
                            : ObservedFetchRunStatus.Failed,
                        reader.GetInt32(4),
                        reader.GetInt32(5) != 0,
                        reader.GetInt32(6) != 0,
                        reader.GetInt32(7) != 0,
                        reader.GetInt32(8) != 0,
                        reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        reader.IsDBNull(11) ? null : reader.GetString(11),
                        reader.IsDBNull(12) ? null : reader.GetString(12)));
                }

                return result;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public ObservedSyncOperationalStatus GetObservedSyncOperationalStatus()
        {
            StateLock.EnterReadLock();
            try
            {
                long productAnchorCount;
                using (var anchorCommand = Connection.CreateCommand())
                {
                    anchorCommand.CommandText = @"
SELECT COUNT(*)
FROM store_properties
WHERE key LIKE 'sync-anchor:mu:%';";
                    productAnchorCount = Convert.ToInt64(
                        anchorCommand.ExecuteScalar(),
                        CultureInfo.InvariantCulture);
                }

                long driverAnchorCount;
                using (var driverCommand = Connection.CreateCommand())
                {
                    driverCommand.CommandText = "SELECT COUNT(*) FROM driver_sync_identifier_anchors;";
                    driverAnchorCount = Convert.ToInt64(
                        driverCommand.ExecuteScalar(),
                        CultureInfo.InvariantCulture);
                }

                using var checkpointCommand = Connection.CreateCommand();
                checkpointCommand.CommandText = @"
SELECT
    COUNT(*),
    COALESCE(SUM(total_items - completed_items), 0),
    COALESCE(SUM(completed_items), 0),
    MIN(created_at),
    MAX(updated_at)
FROM sync_checkpoints;";
                using var reader = checkpointCommand.ExecuteReader();
                reader.Read();
                return new ObservedSyncOperationalStatus(
                    productAnchorCount,
                    driverAnchorCount,
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : ParseCheckpointTimestamp(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : ParseCheckpointTimestamp(reader.GetString(4)));
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public long PruneInactiveDriverSyncState(DateTimeOffset olderThan)
        {
            var cutoff = olderThan
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);

            StateLock.EnterWriteLock();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = @"
DELETE FROM driver_sync_identifier_anchors AS anchor
WHERE anchor.updated_at < $cutoff
  AND NOT EXISTS (
      SELECT 1
      FROM driver_sync_checkpoint_members AS member
      WHERE member.identifier_type = anchor.identifier_type
        AND member.identifier = anchor.identifier
  )
  AND (
      (
          anchor.identifier_type = 1
          AND NOT EXISTS (
              SELECT 1
              FROM observed_pnp_hardware_ids AS observed
              WHERE observed.hardware_id = anchor.identifier
                AND observed.last_seen >= $cutoff
          )
          AND NOT EXISTS (
              SELECT 1
              FROM observed_compatible_ids AS observed
              WHERE observed.compatible_id = anchor.identifier
                AND observed.last_seen >= $cutoff
          )
      )
      OR
      (
          anchor.identifier_type = 2
          AND NOT EXISTS (
              SELECT 1
              FROM observed_computer_ids AS observed
              WHERE lower(observed.computer_id) = lower(anchor.identifier)
                AND observed.last_seen >= $cutoff
          )
      )
  );";
                command.Parameters.AddWithValue("$cutoff", cutoff);
                var deleted = command.ExecuteNonQuery();
                TryReclaimCheckpointStorage();
                return deleted;
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public long CountInactiveDriverSyncState(DateTimeOffset olderThan)
        {
            var cutoff = olderThan
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);

            StateLock.EnterReadLock();
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = @"
SELECT COUNT(*)
FROM driver_sync_identifier_anchors AS anchor
WHERE anchor.updated_at < $cutoff
  AND NOT EXISTS (
      SELECT 1
      FROM driver_sync_checkpoint_members AS member
      WHERE member.identifier_type = anchor.identifier_type
        AND member.identifier = anchor.identifier
  )
  AND (
      (
          anchor.identifier_type = 1
          AND NOT EXISTS (
              SELECT 1
              FROM observed_pnp_hardware_ids AS observed
              WHERE observed.hardware_id = anchor.identifier
                AND observed.last_seen >= $cutoff
          )
          AND NOT EXISTS (
              SELECT 1
              FROM observed_compatible_ids AS observed
              WHERE observed.compatible_id = anchor.identifier
                AND observed.last_seen >= $cutoff
          )
      )
      OR
      (
          anchor.identifier_type = 2
          AND NOT EXISTS (
              SELECT 1
              FROM observed_computer_ids AS observed
              WHERE lower(observed.computer_id) = lower(anchor.identifier)
                AND observed.last_seen >= $cutoff
          )
      )
  );";
                command.Parameters.AddWithValue("$cutoff", cutoff);
                return Convert.ToInt64(
                    command.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        private static List<string> NormalizeObservedIdentifiers(IEnumerable<string> identifiers)
        {
            return (identifiers ?? Array.Empty<string>())
                .Select(NormalizeObservedIdentifier)
                .Where(value => value != null)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxObservedValuesPerType)
                .ToList();
        }

        private static string NormalizeObservedIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return null;
            }

            var normalized = identifier.Trim().ToUpperInvariant();
            if (normalized.Length == 0
                || normalized.Length > MaxObservedIdentifierLength
                || normalized.Any(char.IsControl))
            {
                return null;
            }

            return normalized;
        }

        private void UpsertObservedDetectoids(
            SqliteTransaction transaction,
            IReadOnlyCollection<ObservedDetectoidIdentity> detectoids,
            string observedAt)
        {
            if (detectoids.Count == 0)
            {
                return;
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO observed_detectoids(
    update_id,
    revision_number,
    first_seen,
    last_seen,
    observation_count)
VALUES ($updateId, $revisionNumber, $observedAt, $observedAt, 1)
ON CONFLICT(update_id, revision_number) DO UPDATE SET
    first_seen = MIN(observed_detectoids.first_seen, excluded.first_seen),
    last_seen = MAX(observed_detectoids.last_seen, excluded.last_seen),
    observation_count = observed_detectoids.observation_count + 1;";
            var updateIdParameter = command.Parameters.Add("$updateId", SqliteType.Text);
            var revisionParameter = command.Parameters.Add("$revisionNumber", SqliteType.Integer);
            var observedAtParameter = command.Parameters.Add("$observedAt", SqliteType.Text);
            observedAtParameter.Value = observedAt;
            command.Prepare();

            foreach (var detectoid in detectoids)
            {
                updateIdParameter.Value = detectoid.UpdateId.ToString("D");
                revisionParameter.Value = detectoid.RevisionNumber;
                command.ExecuteNonQuery();
            }
        }

        private void UpsertObservedStringIdentifiers(
            SqliteTransaction transaction,
            string tableName,
            string columnName,
            IReadOnlyCollection<string> identifiers,
            string observedAt)
        {
            if (identifiers.Count == 0)
            {
                return;
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
INSERT INTO {tableName}({columnName}, first_seen, last_seen, observation_count)
VALUES ($identifier, $observedAt, $observedAt, 1)
ON CONFLICT({columnName}) DO UPDATE SET
    first_seen = MIN({tableName}.first_seen, excluded.first_seen),
    last_seen = MAX({tableName}.last_seen, excluded.last_seen),
    observation_count = {tableName}.observation_count + 1;";
            var identifierParameter = command.Parameters.Add("$identifier", SqliteType.Text);
            var observedAtParameter = command.Parameters.Add("$observedAt", SqliteType.Text);
            observedAtParameter.Value = observedAt;
            command.Prepare();

            foreach (var identifier in identifiers)
            {
                identifierParameter.Value = identifier;
                command.ExecuteNonQuery();
            }
        }

        private IReadOnlyList<ObservedIdentifierObservation> ReadObservedStringIdentifiers(
            string tableName,
            string columnName,
            DateTimeOffset? seenSince)
        {
            var result = new List<ObservedIdentifierObservation>();
            using var command = Connection.CreateCommand();
            command.CommandText = $@"
SELECT {columnName}, first_seen, last_seen, observation_count
FROM {tableName}
WHERE $seenSince IS NULL OR last_seen >= $seenSince
ORDER BY last_seen DESC, {columnName};";
            AddObservedSeenSinceParameter(command, seenSince);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ObservedIdentifierObservation(
                    reader.GetString(0),
                    ParseCheckpointTimestamp(reader.GetString(1)),
                    ParseCheckpointTimestamp(reader.GetString(2)),
                    reader.GetInt64(3)));
            }

            return result;
        }

        private ObservedInventoryKindStatus ReadObservedInventoryKindStatus(
            string tableName,
            DateTimeOffset? seenSince)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $@"
SELECT
    COUNT(*),
    COALESCE(SUM(CASE
        WHEN $seenSince IS NULL OR last_seen >= $seenSince THEN 1
        ELSE 0
    END), 0),
    MIN(first_seen),
    MAX(last_seen)
FROM {tableName};";
            AddObservedSeenSinceParameter(command, seenSince);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return new ObservedInventoryKindStatus(0, 0, null, null);
            }

            return new ObservedInventoryKindStatus(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : ParseCheckpointTimestamp(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseCheckpointTimestamp(reader.GetString(3)));
        }

        private static void AddObservedSeenSinceParameter(
            SqliteCommand command,
            DateTimeOffset? seenSince)
        {
            command.Parameters.AddWithValue(
                "$seenSince",
                seenSince.HasValue
                    ? seenSince.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                    : DBNull.Value);
        }

        private long DeleteObservedRows(
            SqliteTransaction transaction,
            string tableName,
            string cutoff)
        {
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {tableName} WHERE last_seen < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            return command.ExecuteNonQuery();
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

        public IReadOnlyList<DriverSyncIdentifierState> GetDriverSyncIdentifierStates(
            string scopeKey,
            IEnumerable<DriverSyncIdentifier> identifiers)
        {
            if (string.IsNullOrWhiteSpace(scopeKey))
            {
                return Array.Empty<DriverSyncIdentifierState>();
            }

            var requestedIdentifiers = new HashSet<DriverSyncIdentifier>(
                (identifiers ?? Array.Empty<DriverSyncIdentifier>())
                    .Where(identifier => identifier != null));
            if (requestedIdentifiers.Count == 0)
            {
                return Array.Empty<DriverSyncIdentifierState>();
            }

            StateLock.EnterReadLock();
            try
            {
                var result = new List<DriverSyncIdentifierState>();
                using var command = Connection.CreateCommand();
                command.CommandText = @"
SELECT identifier_type, identifier, anchor, updated_at
FROM driver_sync_identifier_anchors
WHERE scope_key = $scopeKey
ORDER BY identifier_type, identifier;";
                command.Parameters.AddWithValue("$scopeKey", scopeKey);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var identifier = new DriverSyncIdentifier(
                        (DriverSyncIdentifierType)reader.GetInt32(0),
                        reader.GetString(1));
                    if (!requestedIdentifiers.Contains(identifier))
                    {
                        continue;
                    }

                    result.Add(new DriverSyncIdentifierState(
                        scopeKey,
                        identifier,
                        reader.GetString(2),
                        ParseCheckpointTimestamp(reader.GetString(3))));
                }

                return result;
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public IReadOnlyList<DriverSyncCheckpointInfo> GetDriverSyncCheckpoints(string scopeKey)
        {
            if (string.IsNullOrWhiteSpace(scopeKey))
            {
                return Array.Empty<DriverSyncCheckpointInfo>();
            }

            StateLock.EnterReadLock();
            try
            {
                var checkpointRows = new List<SyncCheckpointInfo>();
                using (var checkpointCommand = Connection.CreateCommand())
                {
                    checkpointCommand.CommandText = @"
SELECT
    checkpoint.anchor_key,
    checkpoint.anchor_from,
    checkpoint.anchor_to,
    checkpoint.total_items,
    checkpoint.completed_items,
    checkpoint.created_at,
    checkpoint.updated_at
FROM driver_sync_checkpoint_scopes AS driver_scope
JOIN sync_checkpoints AS checkpoint
  ON checkpoint.anchor_key = driver_scope.anchor_key
WHERE driver_scope.scope_key = $scopeKey
ORDER BY checkpoint.created_at, checkpoint.anchor_key;";
                    checkpointCommand.Parameters.AddWithValue("$scopeKey", scopeKey);

                    using var checkpointReader = checkpointCommand.ExecuteReader();
                    while (checkpointReader.Read())
                    {
                        checkpointRows.Add(new SyncCheckpointInfo(
                            checkpointReader.GetString(0),
                            checkpointReader.IsDBNull(1) ? null : checkpointReader.GetString(1),
                            checkpointReader.GetString(2),
                            checked((int)checkpointReader.GetInt64(3)),
                            checked((int)checkpointReader.GetInt64(4)),
                            ParseCheckpointTimestamp(checkpointReader.GetString(5)),
                            ParseCheckpointTimestamp(checkpointReader.GetString(6))));
                    }
                }

                var checkpoints = new List<DriverSyncCheckpointInfo>(checkpointRows.Count);
                foreach (var checkpoint in checkpointRows)
                {
                    var members = new List<DriverSyncIdentifier>();
                    using var memberCommand = Connection.CreateCommand();
                    memberCommand.CommandText = @"
SELECT identifier_type, identifier
FROM driver_sync_checkpoint_members
WHERE anchor_key = $anchorKey
ORDER BY identifier_type, identifier;";
                    memberCommand.Parameters.AddWithValue("$anchorKey", checkpoint.AnchorKey);
                    using var memberReader = memberCommand.ExecuteReader();
                    while (memberReader.Read())
                    {
                        members.Add(new DriverSyncIdentifier(
                            (DriverSyncIdentifierType)memberReader.GetInt32(0),
                            memberReader.GetString(1)));
                    }

                    checkpoints.Add(new DriverSyncCheckpointInfo(
                        scopeKey,
                        checkpoint,
                        members));
                }

                return checkpoints;
            }
            finally
            {
                StateLock.ExitReadLock();
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
            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                InsertSyncCheckpoint(
                    transaction,
                    anchorKey,
                    anchorFrom,
                    anchorTo,
                    packageIdentities);
                transaction.Commit();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public void CreateDriverSyncCheckpoint(
            string checkpointAnchorKey,
            string scopeKey,
            string anchorFrom,
            string anchorTo,
            IReadOnlyList<IPackageIdentity> packageIdentities,
            IReadOnlyCollection<DriverSyncIdentifier> identifiers)
        {
            if (string.IsNullOrWhiteSpace(scopeKey))
            {
                throw new ArgumentException("A driver synchronization scope key is required", nameof(scopeKey));
            }

            var normalizedIdentifiers = (identifiers ?? Array.Empty<DriverSyncIdentifier>())
                .Where(identifier => identifier != null)
                .Distinct()
                .OrderBy(identifier => (int)identifier.Type)
                .ThenBy(identifier => identifier.Value, StringComparer.Ordinal)
                .ToList();
            if (normalizedIdentifiers.Count == 0)
            {
                throw new ArgumentException(
                    "At least one driver synchronization identifier is required.",
                    nameof(identifiers));
            }

            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                InsertSyncCheckpoint(
                    transaction,
                    checkpointAnchorKey,
                    anchorFrom,
                    anchorTo,
                    packageIdentities);

                using (var scopeCommand = Connection.CreateCommand())
                {
                    scopeCommand.Transaction = transaction;
                    scopeCommand.CommandText = @"
INSERT INTO driver_sync_checkpoint_scopes(anchor_key, scope_key)
VALUES ($anchorKey, $scopeKey);";
                    scopeCommand.Parameters.AddWithValue("$anchorKey", checkpointAnchorKey);
                    scopeCommand.Parameters.AddWithValue("$scopeKey", scopeKey);
                    scopeCommand.ExecuteNonQuery();
                }

                using (var memberCommand = Connection.CreateCommand())
                {
                    memberCommand.Transaction = transaction;
                    memberCommand.CommandText = @"
INSERT INTO driver_sync_checkpoint_members(anchor_key, identifier_type, identifier)
VALUES ($anchorKey, $identifierType, $identifier);";
                    var anchorKeyParameter = memberCommand.Parameters.Add("$anchorKey", SqliteType.Text);
                    var identifierTypeParameter = memberCommand.Parameters.Add("$identifierType", SqliteType.Integer);
                    var identifierParameter = memberCommand.Parameters.Add("$identifier", SqliteType.Text);
                    memberCommand.Prepare();

                    anchorKeyParameter.Value = checkpointAnchorKey;
                    foreach (var identifier in normalizedIdentifiers)
                    {
                        identifierTypeParameter.Value = (int)identifier.Type;
                        identifierParameter.Value = identifier.Value;
                        memberCommand.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        private long InsertSyncCheckpoint(
            SqliteTransaction transaction,
            string anchorKey,
            string anchorFrom,
            string anchorTo,
            IReadOnlyList<IPackageIdentity> packageIdentities)
        {
            if (transaction == null)
            {
                throw new ArgumentNullException(nameof(transaction));
            }

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
            var identities = requestedIdentities
                .Where(identity => !_IdentityToIndexMap.ContainsKey(identity))
                .ToList();
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

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

            return checkpointId;
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

        public void CompleteDriverSyncCheckpoint(string checkpointAnchorKey)
        {
            if (string.IsNullOrWhiteSpace(checkpointAnchorKey))
            {
                throw new ArgumentException(
                    "A driver checkpoint anchor key is required",
                    nameof(checkpointAnchorKey));
            }

            StateLock.EnterWriteLock();
            try
            {
                using var transaction = Connection.BeginTransaction();
                string scopeKey;
                string anchorTo;

                using (var checkpointCommand = Connection.CreateCommand())
                {
                    checkpointCommand.Transaction = transaction;
                    checkpointCommand.CommandText = @"
SELECT
    driver_scope.scope_key,
    checkpoint.anchor_to,
    checkpoint.total_items,
    checkpoint.completed_items
FROM driver_sync_checkpoint_scopes AS driver_scope
JOIN sync_checkpoints AS checkpoint
  ON checkpoint.anchor_key = driver_scope.anchor_key
WHERE checkpoint.anchor_key = $anchorKey;";
                    checkpointCommand.Parameters.AddWithValue("$anchorKey", checkpointAnchorKey);
                    using var reader = checkpointCommand.ExecuteReader();
                    if (!reader.Read())
                    {
                        throw new InvalidOperationException(
                            $"No driver synchronization checkpoint exists for {checkpointAnchorKey}");
                    }

                    scopeKey = reader.GetString(0);
                    anchorTo = reader.GetString(1);
                    var totalItems = reader.GetInt64(2);
                    var completedItems = reader.GetInt64(3);
                    if (completedItems != totalItems)
                    {
                        throw new InvalidOperationException(
                            $"Cannot promote driver anchors while " +
                            $"{totalItems - completedItems} checkpoint item(s) are pending");
                    }
                }

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
                    pendingCommand.Parameters.AddWithValue("$anchorKey", checkpointAnchorKey);
                    var pendingCount = (long)pendingCommand.ExecuteScalar();
                    if (pendingCount != 0)
                    {
                        throw new InvalidOperationException(
                            $"Cannot promote driver anchors while {pendingCount} checkpoint item(s) are pending");
                    }
                }

                var members = new List<(int Type, string Value)>();
                using (var memberCommand = Connection.CreateCommand())
                {
                    memberCommand.Transaction = transaction;
                    memberCommand.CommandText = @"
SELECT identifier_type, identifier
FROM driver_sync_checkpoint_members
WHERE anchor_key = $anchorKey
ORDER BY identifier_type, identifier;";
                    memberCommand.Parameters.AddWithValue("$anchorKey", checkpointAnchorKey);
                    using var reader = memberCommand.ExecuteReader();
                    while (reader.Read())
                    {
                        members.Add((reader.GetInt32(0), reader.GetString(1)));
                    }
                }

                if (members.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Driver checkpoint {checkpointAnchorKey} has no identifier members");
                }

                var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                using (var stateCommand = Connection.CreateCommand())
                {
                    stateCommand.Transaction = transaction;
                    stateCommand.CommandText = @"
INSERT INTO driver_sync_identifier_anchors(
    scope_key,
    identifier_type,
    identifier,
    anchor,
    updated_at)
VALUES ($scopeKey, $identifierType, $identifier, $anchor, $updatedAt)
ON CONFLICT(scope_key, identifier_type, identifier) DO UPDATE SET
    anchor = excluded.anchor,
    updated_at = excluded.updated_at;";
                    var scopeKeyParameter = stateCommand.Parameters.Add("$scopeKey", SqliteType.Text);
                    var identifierTypeParameter = stateCommand.Parameters.Add("$identifierType", SqliteType.Integer);
                    var identifierParameter = stateCommand.Parameters.Add("$identifier", SqliteType.Text);
                    var anchorParameter = stateCommand.Parameters.Add("$anchor", SqliteType.Text);
                    var updatedAtParameter = stateCommand.Parameters.Add("$updatedAt", SqliteType.Text);
                    stateCommand.Prepare();

                    scopeKeyParameter.Value = scopeKey;
                    anchorParameter.Value = anchorTo;
                    updatedAtParameter.Value = now;
                    foreach (var member in members)
                    {
                        identifierTypeParameter.Value = member.Type;
                        identifierParameter.Value = member.Value;
                        stateCommand.ExecuteNonQuery();
                    }
                }

                using (var deleteCommand = Connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText =
                        "DELETE FROM sync_checkpoints WHERE anchor_key = $anchorKey;";
                    deleteCommand.Parameters.AddWithValue("$anchorKey", checkpointAnchorKey);
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

        public void ClearDriverSyncCheckpoint(string checkpointAnchorKey)
        {
            ClearSyncCheckpoint(checkpointAnchorKey);
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

                if (typeof(T) == typeof(UpdateFile))
                {
                    var parsedFiles = ReadFilesFromMetadata(packageIndex);
                    if (parsedFiles.Count > 0)
                    {
                        return parsedFiles.Cast<T>().ToList();
                    }
                }

                // Compatibility fallback for databases created by earlier patches that stored
                // full UpdateFile JSON blobs in SQLite. New stores no longer write this payload.
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
SELECT fl.sha1_base64, fl.mu_url
FROM package_file_map pfm
JOIN file_locations fl ON fl.sha1_base64 = pfm.sha1_base64
WHERE pfm.package_index = $packageIndex
  AND fl.mu_url IS NOT NULL;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var digestBase64 = reader.GetString(0);
                var url = reader.GetString(1);
                urls[digestBase64] = new UpdateFileUrl(digestBase64, url, null);
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
                    InsertClientSyncMetadata(package, packageIndex, transaction);
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

                if (stagedPackages.Count > 0 && IsCatalogGenerationPublicationDeferred)
                {
                    using var unpublishedCommand = Connection.CreateCommand();
                    unpublishedCommand.Transaction = transaction;
                    unpublishedCommand.CommandText = @"
INSERT INTO store_properties(key, value)
VALUES ($key, '1')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                    unpublishedCommand.Parameters.AddWithValue(
                        "$key",
                        CatalogUnpublishedChangesKey);
                    unpublishedCommand.ExecuteNonQuery();
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
                    CatalogGenerationDirty = true;
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
INSERT INTO packages(package_index, partition, open_id_hex, identity, package_type, published, metadata, metadata_compression, files_json, files_blob, files_compression)
VALUES ($packageIndex, $partition, $openIdHex, $identity, $packageType, $published, $metadata, $metadataCompression, NULL, $filesBlob, $filesCompression);";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            command.Parameters.AddWithValue("$partition", package.Id.Partition);
            command.Parameters.AddWithValue("$openIdHex", package.Id.OpenIdHex);
            command.Parameters.AddWithValue("$identity", package.Id.ToString());
            command.Parameters.AddWithValue("$packageType", packageType);
            command.Parameters.AddWithValue("$published", IsCatalogGenerationPublicationDeferred ? 0 : 1);
            command.Parameters.AddWithValue("$metadata", metadataBytes);
            command.Parameters.AddWithValue("$metadataCompression", metadataCompression);
            command.Parameters.AddWithValue("$filesBlob", (object)filesBlob ?? DBNull.Value);
            command.Parameters.AddWithValue("$filesCompression", filesBlob == null ? DBNull.Value : (object)CompressionBrotli);
            command.ExecuteNonQuery();
        }

        private void InsertClientSyncMetadata(IPackage package, int packageIndex, SqliteTransaction transaction)
        {
            if (package is not MicrosoftUpdatePackage microsoftUpdatePackage)
            {
                return;
            }

            using (var packageCommand = Connection.CreateCommand())
            {
                packageCommand.Transaction = transaction;
                packageCommand.CommandText = @"
INSERT INTO client_sync_packages(package_index, update_id, revision_number)
VALUES ($packageIndex, $updateId, $revisionNumber);";
                packageCommand.Parameters.AddWithValue("$packageIndex", packageIndex);
                packageCommand.Parameters.AddWithValue("$updateId", microsoftUpdatePackage.Id.ID.ToString("D"));
                packageCommand.Parameters.AddWithValue("$revisionNumber", microsoftUpdatePackage.Id.Revision);
                packageCommand.ExecuteNonQuery();
            }

            InsertClientSyncPrerequisites(microsoftUpdatePackage, packageIndex, transaction);

            if (microsoftUpdatePackage is SoftwareUpdate softwareUpdate)
            {
                InsertClientSyncSupersedence(softwareUpdate, packageIndex, transaction);
                InsertClientSyncBundles(softwareUpdate, packageIndex, transaction);
            }

            if (microsoftUpdatePackage is DriverUpdate driverUpdate)
            {
                InsertClientSyncDriverHardwareIds(driverUpdate, packageIndex, transaction);
            }
        }

        private void InsertClientSyncPrerequisites(
            MicrosoftUpdatePackage package,
            int packageIndex,
            SqliteTransaction transaction)
        {
            var prerequisites = package.Prerequisites;
            if (prerequisites == null || prerequisites.Count == 0)
            {
                return;
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR IGNORE INTO client_sync_prerequisites(
    package_index,
    prerequisite_update_id,
    is_category)
VALUES ($packageIndex, $updateId, $isCategory);";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            var updateIdParameter = command.Parameters.Add("$updateId", SqliteType.Text);
            var isCategoryParameter = command.Parameters.Add("$isCategory", SqliteType.Integer);
            command.Prepare();

            foreach (var prerequisite in prerequisites)
            {
                if (prerequisite is Simple simple)
                {
                    updateIdParameter.Value = simple.UpdateId.ToString("D");
                    isCategoryParameter.Value = 0;
                    command.ExecuteNonQuery();
                }
                else if (prerequisite is AtLeastOne atLeastOne)
                {
                    foreach (var item in atLeastOne.Simple ?? Enumerable.Empty<Simple>())
                    {
                        updateIdParameter.Value = item.UpdateId.ToString("D");
                        isCategoryParameter.Value = atLeastOne.IsCategory ? 1 : 0;
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        private void InsertClientSyncSupersedence(
            SoftwareUpdate package,
            int packageIndex,
            SqliteTransaction transaction)
        {
            var supersededUpdates = package.SupersededUpdates;
            if (supersededUpdates == null || supersededUpdates.Count == 0)
            {
                return;
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR IGNORE INTO client_sync_supersedence(
    superseding_package_index,
    superseded_update_id)
VALUES ($packageIndex, $updateId);";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            var updateIdParameter = command.Parameters.Add("$updateId", SqliteType.Text);
            command.Prepare();

            foreach (var updateId in supersededUpdates.Where(value => value != Guid.Empty))
            {
                updateIdParameter.Value = updateId.ToString("D");
                command.ExecuteNonQuery();
            }
        }

        private void InsertClientSyncBundles(
            SoftwareUpdate package,
            int packageIndex,
            SqliteTransaction transaction)
        {
            var bundledUpdates = package.BundledUpdates;
            if (bundledUpdates == null || bundledUpdates.Count == 0)
            {
                return;
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR IGNORE INTO client_sync_bundles(
    bundle_package_index,
    bundled_update_id,
    bundled_revision_number)
VALUES ($packageIndex, $updateId, $revisionNumber);";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            var updateIdParameter = command.Parameters.Add("$updateId", SqliteType.Text);
            var revisionParameter = command.Parameters.Add("$revisionNumber", SqliteType.Integer);
            command.Prepare();

            foreach (var identity in bundledUpdates.Where(value => value != null))
            {
                updateIdParameter.Value = identity.ID.ToString("D");
                revisionParameter.Value = identity.Revision;
                command.ExecuteNonQuery();
            }
        }

        private void InsertClientSyncDriverHardwareIds(
            DriverUpdate package,
            int packageIndex,
            SqliteTransaction transaction)
        {
            var metadata = package.GetDriverMetadata();
            if (metadata == null || metadata.Count == 0)
            {
                return;
            }

            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR IGNORE INTO client_sync_driver_hardware_ids(
    package_index,
    metadata_ordinal,
    hardware_id)
VALUES ($packageIndex, $metadataOrdinal, $hardwareId);";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            var ordinalParameter = command.Parameters.Add("$metadataOrdinal", SqliteType.Integer);
            var hardwareIdParameter = command.Parameters.Add("$hardwareId", SqliteType.Text);
            command.Prepare();

            for (var index = 0; index < metadata.Count; index++)
            {
                var hardwareId = metadata[index]?.HardwareID;
                if (string.IsNullOrWhiteSpace(hardwareId))
                {
                    continue;
                }

                ordinalParameter.Value = index;
                hardwareIdParameter.Value = hardwareId.Trim().ToLowerInvariant();
                command.ExecuteNonQuery();
            }
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

                log?.Invoke("Pruning duplicated package file metadata stored in SQLite...");
                var prunedPackageFileRows = PrunePackageFiles(log);

                log?.Invoke("Rebuilding compact SHA1 -> Microsoft URL lookup table...");
                var rebuiltFileLocationRows = RebuildFileLocationsTableCompact(log);

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
                WriteProperty("files_storage", "metadata-xml-plus-file-location-map");
                WriteProperty("file_locations_storage", "sha1-url-only");

                ExecuteNonQuery("PRAGMA optimize;");
                ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");

                log?.Invoke($"SQLite store optimization done. Compressed metadata rows: {compressedMetadataRows}; pruned package file rows: {prunedPackageFileRows}; file-location rows: {rebuiltFileLocationRows}.");
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

        public MetadataStoreGenerationInfo GetLoadedMetadataGeneration()
        {
            StateLock.EnterReadLock();
            try
            {
                return LoadedMetadataGeneration
                    ?? new MetadataStoreGenerationInfo(0, DateTimeOffset.MinValue);
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public MetadataStoreGenerationInfo GetPersistentMetadataGeneration()
        {
            StateLock.EnterReadLock();
            try
            {
                return ReadMetadataGenerationInfo();
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        public void DeferCatalogPublication()
        {
            StateLock.EnterWriteLock();
            try
            {
                IsCatalogGenerationPublicationDeferred = true;
                WriteProperty(CatalogPublicationDeferredKey, "1");
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public MetadataStoreGenerationInfo PublishDeferredCatalogChanges()
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

                if (CatalogGenerationDirty)
                {
                    LoadedMetadataGeneration = PublishCatalogGeneration();
                    CatalogGenerationDirty = false;
                }
                else
                {
                    ClearCatalogPublicationState();
                }

                return LoadedMetadataGeneration
                    ?? new MetadataStoreGenerationInfo(0, DateTimeOffset.MinValue);
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
        }

        public bool ReloadMetadataIfChanged()
        {
            StateLock.EnterWriteLock();
            try
            {
                var persistentGeneration = ReadMetadataGenerationInfo();
                var loadedGeneration = LoadedMetadataGeneration
                    ?? new MetadataStoreGenerationInfo(0, DateTimeOffset.MinValue);
                if (persistentGeneration.Generation <= loadedGeneration.Generation)
                {
                    return false;
                }

                // Build the complete replacement state first. The currently loaded
                // maps and index container remain usable if any read/validation step
                // fails, so the update server can continue serving its old generation.
                var replacementIdentityToIndex = new Dictionary<IPackageIdentity, int>();
                var replacementIndexToIdentity = new Dictionary<int, IPackageIdentity>();
                var replacementPackageTypes = new Dictionary<int, int>();

                using (var packageCommand = Connection.CreateCommand())
                {
                    packageCommand.CommandText = @"
SELECT package_index, identity, package_type
FROM packages
ORDER BY package_index;";
                    using var reader = packageCommand.ExecuteReader();
                    while (reader.Read())
                    {
                        var packageIndex = reader.GetInt32(0);
                        var identity = IdentityFromString(reader.GetString(1));
                        var packageType = reader.GetInt32(2);
                        replacementIdentityToIndex.Add(identity, packageIndex);
                        replacementIndexToIdentity.Add(packageIndex, identity);
                        replacementPackageTypes.Add(packageIndex, packageType);
                    }
                }

                MemoryStream replacementIndexStream = null;
                ZipStreamIndexContainer replacementIndexes = null;
                try
                {
                    var indexData = ReadBlob(IndexBlobKey);
                    if (indexData != null && indexData.Length > 0)
                    {
                        replacementIndexStream = new MemoryStream(indexData, false);
                        replacementIndexes = ZipStreamIndexContainer.Open(replacementIndexStream);
                        if (replacementIndexes.GetStatus() != ZipStreamIndexContainer.IndexContainerStatus.Valid)
                        {
                            throw new InvalidDataException(
                                "The newly published metadata index is invalid.");
                        }
                    }
                    else
                    {
                        replacementIndexes = ZipStreamIndexContainer.Create();
                        if (replacementIdentityToIndex.Count > 0)
                        {
                            throw new InvalidDataException(
                                "The newly published catalog contains packages but no metadata index.");
                        }
                    }

                    var indexedPackageCountValue = ReadProperty("indexed_package_count");
                    if (!int.TryParse(
                            indexedPackageCountValue,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var indexedPackageCount)
                        || indexedPackageCount != replacementIdentityToIndex.Count)
                    {
                        throw new InvalidDataException(
                            $"The newly published metadata index covers {indexedPackageCountValue ?? "an unknown number of"} " +
                            $"package(s), while SQLite contains {replacementIdentityToIndex.Count}.");
                    }

                    var previousIndexes = Indexes;
                    var previousIndexStream = IndexBackingStream;

                    _IdentityToIndexMap = replacementIdentityToIndex;
                    _IndexToIdentityMap = replacementIndexToIdentity;
                    _PackageTypeIndex = replacementPackageTypes;
                    _NextPackageIndex = replacementIndexToIdentity.Count == 0
                        ? 0
                        : replacementIndexToIdentity.Keys.Max() + 1;
                    Indexes = replacementIndexes;
                    IndexBackingStream = replacementIndexStream;
                    replacementIndexes = null;
                    replacementIndexStream = null;
                    PendingPackages.Clear();
                    IsDirty = false;
                    IsIndexDirty = false;
                    CatalogGenerationDirty = false;
                    _IsReindexingRequired = false;
                    LoadedMetadataGeneration = persistentGeneration;

                    try
                    {
                        previousIndexes?.CloseInput();
                        previousIndexStream?.Dispose();
                    }
                    catch
                    {
                        // The replacement state is already active. Disposal of
                        // the old read-only index stream is best-effort.
                    }
                    return true;
                }
                finally
                {
                    replacementIndexes?.CloseInput();
                    replacementIndexStream?.Dispose();
                }
            }
            finally
            {
                StateLock.ExitWriteLock();
            }
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

                if (CatalogGenerationDirty && !IsCatalogGenerationPublicationDeferred)
                {
                    LoadedMetadataGeneration = PublishCatalogGeneration();
                    CatalogGenerationDirty = false;
                }
                else if (CatalogGenerationDirty)
                {
                    PersistUnpublishedCatalogChanges();
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
                CatalogGenerationDirty = true;
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
