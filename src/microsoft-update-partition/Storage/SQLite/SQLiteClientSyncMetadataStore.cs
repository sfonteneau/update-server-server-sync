// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Drivers;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Parsers;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.XPath;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Client-facing SQLite read model. Every operation opens a short-lived SQLite
    /// connection and uses precomputed SQLite read models. A bounded metadata LRU
    /// avoids repeatedly decompressing and parsing the same selected packages.
    /// </summary>
    internal sealed class SQLiteClientSyncMetadataStore : IClientSyncProjectionStore
    {
        private const int RequiredSchemaVersion = 10;
        private const int MaxObservedValuesPerType = 20000;
        private const int MaxObservedIdentifierLength = 2048;
        private const int MetadataCacheCapacity = 128;
        private const long MetadataCacheMaxBytes = 64L * 1024L * 1024L;
        private const string CompressionNone = "none";
        private const string CompressionBrotli = "br";
        private const string CompressionGZip = "gzip";

        private readonly string DatabasePath;
        private readonly string ConnectionString;
        private readonly object MetadataCacheLock = new();
        private readonly Dictionary<int, LinkedListNode<MetadataCacheEntry>> MetadataCache = new();
        private readonly LinkedList<MetadataCacheEntry> MetadataCacheLru = new();
        private long MetadataCacheBytes;
        private long MetadataCacheGeneration = -1;
        private long MetadataCacheHits;
        private long MetadataCacheMisses;
        private bool IsDisposed;

        public SQLiteClientSyncMetadataStore(string storePath)
        {
            if (string.IsNullOrWhiteSpace(storePath))
            {
                throw new ArgumentException("A metadata store path is required.", nameof(storePath));
            }

            DatabasePath = Path.Combine(storePath, SQLitePackageStore.DatabaseFileName);
            if (!File.Exists(DatabasePath))
            {
                throw new FileNotFoundException("The SQLite metadata store does not exist.", DatabasePath);
            }

            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                // Shared-cache mode adds table-level locks that return SQLITE_LOCKED
                // immediately under concurrent inventory writers. Private page caches
                // preserve WAL concurrency; connections are short-lived and SQLite's
                // default cache remains bounded per connection.
                Cache = SqliteCacheMode.Private
            }.ToString();

            using var connection = OpenConnection();
            ClientSyncFragmentSchemaMigrator.TryMigrate(connection);
            ValidateSchema(connection);
        }

        public MetadataStoreGenerationInfo GetPublishedCatalogInfo()
        {
            ThrowIfDisposed();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    COALESCE(MAX(CASE WHEN key = 'catalog_generation' THEN value END), '0'),
    COALESCE(MAX(CASE WHEN key = 'catalog_last_changed' THEN value END), ''),
    COALESCE(MAX(CASE WHEN key = 'catalog_publication_deferred' THEN value END), '0'),
    COALESCE(MAX(CASE WHEN key = 'catalog_unpublished_changes' THEN value END), '0')
FROM store_properties
WHERE key IN (
    'catalog_generation',
    'catalog_last_changed',
    'catalog_publication_deferred',
    'catalog_unpublished_changes');";
            using var reader = command.ExecuteReader();
            reader.Read();

            long generation = 0;
            long.TryParse(
                reader.GetString(0),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out generation);

            var lastChanged = DateTimeOffset.MinValue;
            if (!reader.IsDBNull(1)
                && DateTimeOffset.TryParse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                lastChanged = parsed.ToUniversalTime();
            }

            return new MetadataStoreGenerationInfo(
                generation,
                lastChanged,
                publicationDeferred: string.Equals(
                    reader.GetString(2),
                    "1",
                    StringComparison.Ordinal),
                hasUnpublishedChanges: string.Equals(
                    reader.GetString(3),
                    "1",
                    StringComparison.Ordinal));
        }

        public IReadOnlyDictionary<int, MicrosoftUpdatePackageIdentity> GetPackageIdentities(
            IEnumerable<int> revisionIds)
        {
            return ReadPackageIdentities(revisionIds, observedDetectorsOnly: false);
        }

        public IReadOnlyDictionary<int, MicrosoftUpdatePackageIdentity> GetObservedDetectorIdentities(
            IEnumerable<int> revisionIds)
        {
            return ReadPackageIdentities(revisionIds, observedDetectorsOnly: true);
        }

        public bool TryGetPackageIdentity(int revisionId, out MicrosoftUpdatePackageIdentity identity)
        {
            ThrowIfDisposed();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT identity
FROM packages
WHERE package_index = $revisionId
  AND published = 1;";
            command.Parameters.AddWithValue("$revisionId", revisionId);
            var value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value)
            {
                identity = null;
                return false;
            }

            identity = MicrosoftUpdatePackageIdentity.FromString((string)value);
            return true;
        }

        public bool TryGetDetectoidIdentity(int revisionId, out MicrosoftUpdatePackageIdentity identity)
        {
            ThrowIfDisposed();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT identity
FROM packages
WHERE package_index = $revisionId
  AND package_type = $detectoidType
  AND published = 1;";
            command.Parameters.AddWithValue("$revisionId", revisionId);
            command.Parameters.AddWithValue(
                "$detectoidType",
                (int)StoredPackageType.MicrosoftUpdateDetectoid);
            var value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value)
            {
                identity = null;
                return false;
            }

            identity = MicrosoftUpdatePackageIdentity.FromString((string)value);
            return true;
        }

        public bool TryGetPackage(int revisionId, out ClientSyncPackageRecord package)
        {
            ThrowIfDisposed();
            using var connection = OpenConnection();
            package = ReadPackageRecord(
                connection,
                "p.package_index = $value",
                revisionId,
                parameterIsIdentity: false);
            return package != null;
        }

        public bool TryGetPackage(
            MicrosoftUpdatePackageIdentity identity,
            out ClientSyncPackageRecord package)
        {
            ThrowIfDisposed();
            if (identity == null)
            {
                package = null;
                return false;
            }

            using var connection = OpenConnection();
            package = ReadPackageRecord(
                connection,
                "p.identity = $value",
                identity.ToString(),
                parameterIsIdentity: true);
            return package != null;
        }

        public int GetRevisionId(MicrosoftUpdatePackageIdentity identity)
        {
            ThrowIfDisposed();
            if (identity == null)
            {
                return -1;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT package_index
FROM packages
WHERE identity = $identity
  AND published = 1;";
            command.Parameters.AddWithValue("$identity", identity.ToString());
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value
                ? -1
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        public Stream GetMetadata(MicrosoftUpdatePackageIdentity identity)
        {
            ThrowIfDisposed();
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            using var connection = OpenConnection();
            EnsureMetadataCacheGeneration(connection);
            var bytes = ReadMetadataBytesByIdentity(connection, identity);
            return new MemoryStream(bytes, writable: false);
        }

        public IReadOnlyList<UpdateFile> GetFiles(MicrosoftUpdatePackageIdentity identity)
        {
            ThrowIfDisposed();
            if (identity == null)
            {
                return Array.Empty<UpdateFile>();
            }

            using var connection = OpenConnection();
            EnsureMetadataCacheGeneration(connection);
            var packageIndex = GetPublishedPackageIndex(connection, identity);
            if (packageIndex < 0)
            {
                return Array.Empty<UpdateFile>();
            }

            var metadataBytes = ReadMetadataBytes(connection, packageIndex);
            using var metadataStream = new MemoryStream(metadataBytes, writable: false);
            XPathDocument document = new(metadataStream);
            XPathNavigator navigator = document.CreateNavigator();
            XmlNamespaceManager manager = new(navigator.NameTable);
            manager.AddNamespace("upd", "http://schemas.microsoft.com/msus/2002/12/Update");

            var files = UpdateFileParser.ParseFiles(navigator, manager);
            if (files.Count == 0)
            {
                return files;
            }

            var urlMap = ReadPackageFileUrls(connection, packageIndex);
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

        public IReadOnlyList<ClientSyncPackageRecord> GetSoftwareCandidates(
            ClientSyncSoftwareStage stage,
            IReadOnlyCollection<Guid> installedNonLeafUpdateIds,
            IReadOnlyCollection<Guid> otherCachedUpdateIds,
            bool approveAllSoftwareUpdates,
            IReadOnlyCollection<MicrosoftUpdatePackageIdentity> approvedSoftwareUpdates,
            int maxResults,
            out bool truncated)
        {
            ThrowIfDisposed();
            if (maxResults <= 0)
            {
                truncated = false;
                return Array.Empty<ClientSyncPackageRecord>();
            }

            var excluded = new HashSet<Guid>(installedNonLeafUpdateIds ?? Array.Empty<Guid>());
            excluded.UnionWith(otherCachedUpdateIds ?? Array.Empty<Guid>());
            var selected = new List<(int PackageIndex, bool IsBundle, bool IsBundled)>(maxResults + 1);
            using var connection = OpenConnection();
            EnsureMetadataCacheGeneration(connection);
            PopulateTemporaryGuidTable(
                connection,
                "temp_client_sync_installed",
                installedNonLeafUpdateIds ?? Array.Empty<Guid>());
            PopulateTemporaryGuidTable(connection, "temp_client_sync_excluded", excluded);
            PopulateTemporaryTextTable(
                connection,
                "temp_client_sync_approved",
                (approvedSoftwareUpdates ?? Array.Empty<MicrosoftUpdatePackageIdentity>())
                    .Where(identity => identity != null)
                    .Select(identity => identity.ToString()));

            using var command = connection.CreateCommand();
            command.CommandText = BuildSoftwareCandidateQuery(stage, includeCoreXml: false);
            command.Parameters.AddWithValue("$driverType", (int)StoredPackageType.MicrosoftUpdateDriver);
            command.Parameters.AddWithValue("$softwareType", (int)StoredPackageType.MicrosoftUpdateSoftware);
            command.Parameters.AddWithValue("$approveAll", approveAllSoftwareUpdates ? 1 : 0);
            command.Parameters.AddWithValue("$limit", maxResults + 1);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    selected.Add((
                        reader.GetInt32(0),
                        reader.GetInt64(1) != 0,
                        reader.GetInt64(2) != 0));
                }
            }

            truncated = selected.Count > maxResults;
            if (truncated)
            {
                selected.RemoveAt(selected.Count - 1);
            }

            var results = new List<ClientSyncPackageRecord>(selected.Count);
            foreach (var candidate in selected)
            {
                var package = LoadPackageByIndex(connection, candidate.PackageIndex);
                if (package != null)
                {
                    results.Add(new ClientSyncPackageRecord(
                        candidate.PackageIndex,
                        package,
                        candidate.IsBundle,
                        candidate.IsBundled));
                }
            }

            return results;
        }

        public IReadOnlyList<ClientSyncSoftwareCandidateRecord> GetSoftwareCandidateProjections(
            ClientSyncSoftwareStage stage,
            IReadOnlyCollection<Guid> installedNonLeafUpdateIds,
            IReadOnlyCollection<Guid> otherCachedUpdateIds,
            bool approveAllSoftwareUpdates,
            IReadOnlyCollection<MicrosoftUpdatePackageIdentity> approvedSoftwareUpdates,
            int maxResults,
            out bool truncated)
        {
            ThrowIfDisposed();
            if (maxResults <= 0)
            {
                truncated = false;
                return Array.Empty<ClientSyncSoftwareCandidateRecord>();
            }

            var totalStopwatch = Stopwatch.StartNew();
            var excluded = new HashSet<Guid>(installedNonLeafUpdateIds ?? Array.Empty<Guid>());
            excluded.UnionWith(otherCachedUpdateIds ?? Array.Empty<Guid>());
            var results = new List<ClientSyncSoftwareCandidateRecord>(maxResults + 1);

            using var connection = OpenConnection();
            PopulateTemporaryGuidTable(
                connection,
                "temp_client_sync_installed",
                installedNonLeafUpdateIds ?? Array.Empty<Guid>());
            PopulateTemporaryGuidTable(
                connection,
                "temp_client_sync_excluded",
                excluded);
            PopulateTemporaryTextTable(
                connection,
                "temp_client_sync_approved",
                (approvedSoftwareUpdates ?? Array.Empty<MicrosoftUpdatePackageIdentity>())
                    .Where(identity => identity != null)
                    .Select(identity => identity.ToString()));

            using var command = connection.CreateCommand();
            command.CommandText = BuildSoftwareCandidateQuery(stage, includeCoreXml: true);
            command.Parameters.AddWithValue("$driverType", (int)StoredPackageType.MicrosoftUpdateDriver);
            command.Parameters.AddWithValue("$softwareType", (int)StoredPackageType.MicrosoftUpdateSoftware);
            command.Parameters.AddWithValue("$approveAll", approveAllSoftwareUpdates ? 1 : 0);
            command.Parameters.AddWithValue("$limit", maxResults + 1);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    results.Add(new ClientSyncSoftwareCandidateRecord(
                        reader.GetInt32(0),
                        reader.GetString(3),
                        reader.GetInt64(1) != 0,
                        reader.GetInt64(2) != 0));
                }
            }

            truncated = results.Count > maxResults;
            if (truncated)
            {
                results.RemoveAt(results.Count - 1);
            }

            totalStopwatch.Stop();
            Trace.TraceInformation(
                $"Client software scan stage={stage}: returned={results.Count}, " +
                $"truncated={truncated}, total_ms={totalStopwatch.ElapsedMilliseconds}.");
            return results;
        }

        public bool HasLaterSoftwareCandidates(
            ClientSyncSoftwareStage currentStage,
            IReadOnlyCollection<Guid> installedNonLeafUpdateIds,
            IReadOnlyCollection<Guid> otherCachedUpdateIds,
            bool approveAllSoftwareUpdates,
            IReadOnlyCollection<MicrosoftUpdatePackageIdentity> approvedSoftwareUpdates)
        {
            ThrowIfDisposed();
            if (currentStage == ClientSyncSoftwareStage.Leaf)
            {
                return false;
            }

            var excluded = new HashSet<Guid>(installedNonLeafUpdateIds ?? Array.Empty<Guid>());
            excluded.UnionWith(otherCachedUpdateIds ?? Array.Empty<Guid>());

            using var connection = OpenConnection();
            PopulateTemporaryGuidTable(
                connection,
                "temp_client_sync_excluded",
                excluded);
            PopulateTemporaryTextTable(
                connection,
                "temp_client_sync_approved",
                (approvedSoftwareUpdates ?? Array.Empty<MicrosoftUpdatePackageIdentity>())
                    .Where(identity => identity != null)
                    .Select(identity => identity.ToString()));

            var laterStagePredicate = currentStage switch
            {
                ClientSyncSoftwareStage.Root => @"
(
    graph.package_type < $driverType
    AND graph.has_prerequisites = 1
    AND graph.has_dependents = 1
)
OR (
    graph.package_type = $softwareType
    AND graph.has_prerequisites = 1
    AND graph.has_dependents = 0
    AND graph.is_superseded = 0
)",
                ClientSyncSoftwareStage.NonLeaf => @"
graph.package_type = $softwareType
AND graph.has_prerequisites = 1
AND graph.has_dependents = 0
AND graph.is_superseded = 0",
                ClientSyncSoftwareStage.BundledLeaf => @"
graph.package_type = $softwareType
AND graph.has_prerequisites = 1
AND graph.has_dependents = 0
AND graph.is_superseded = 0
AND graph.is_bundled = 0",
                _ => "0"
            };

            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT 1
FROM client_sync_graph AS graph
JOIN packages AS package
  ON package.package_index = graph.package_index
 AND package.published = 1
LEFT JOIN temp_client_sync_excluded AS excluded
  ON excluded.value = graph.update_id
LEFT JOIN temp_client_sync_approved AS approved
  ON approved.value = package.identity
WHERE excluded.value IS NULL
  AND ({laterStagePredicate})
  AND (
      $approveAll = 1
      OR graph.package_type <> $softwareType
      OR approved.value IS NOT NULL
  )
LIMIT 1;";
            command.Parameters.AddWithValue("$driverType", (int)StoredPackageType.MicrosoftUpdateDriver);
            command.Parameters.AddWithValue("$softwareType", (int)StoredPackageType.MicrosoftUpdateSoftware);
            command.Parameters.AddWithValue("$approveAll", approveAllSoftwareUpdates ? 1 : 0);
            return command.ExecuteScalar() != null;
        }

        public DriverMatchResult MatchDriver(
            IEnumerable<string> hardwareIds,
            IEnumerable<Guid> computerHardwareIds,
            IReadOnlyCollection<Guid> installedPrerequisites)
        {
            var results = MatchDrivers(
                new[] { new ClientSyncDriverMatchRequest(hardwareIds) },
                computerHardwareIds,
                installedPrerequisites);
            return results.Count == 0 ? null : results[0]?.MatchResult;
        }

        public IReadOnlyList<ClientSyncDriverMatchRecord> MatchDrivers(
            IReadOnlyList<ClientSyncDriverMatchRequest> requests,
            IEnumerable<Guid> computerHardwareIds,
            IReadOnlyCollection<Guid> installedPrerequisites)
        {
            ThrowIfDisposed();
            var requestedDevices = requests ?? Array.Empty<ClientSyncDriverMatchRequest>();
            if (requestedDevices.Count == 0)
            {
                return Array.Empty<ClientSyncDriverMatchRecord>();
            }

            var computerIds = (computerHardwareIds ?? Array.Empty<Guid>()).ToList();
            var results = new List<ClientSyncDriverMatchRecord>(requestedDevices.Count);
            var responseProjectionByPackage = new Dictionary<int, ClientSyncResponseProjection>();
            var stopwatch = Stopwatch.StartNew();
            var matchedCount = 0;

            using var connection = OpenConnection();
            EnsureMetadataCacheGeneration(connection);
            PopulateTemporaryGuidTable(
                connection,
                "temp_client_sync_installed",
                installedPrerequisites ?? Array.Empty<Guid>());

            foreach (var request in requestedDevices)
            {
                var result = MatchDriver(
                    connection,
                    request?.HardwareIds ?? Array.Empty<string>(),
                    computerIds,
                    responseProjectionByPackage);
                results.Add(result);
                if (result != null)
                {
                    matchedCount++;
                }
            }

            stopwatch.Stop();
            Trace.TraceInformation(
                $"Client driver batch: devices={requestedDevices.Count}, matched={matchedCount}, " +
                $"unique_packages={responseProjectionByPackage.Count}, total_ms={stopwatch.ElapsedMilliseconds}.");
            return results;
        }

        private ClientSyncDriverMatchRecord MatchDriver(
            SqliteConnection connection,
            IEnumerable<string> hardwareIds,
            IReadOnlyList<Guid> computerHardwareIds,
            IDictionary<int, ClientSyncResponseProjection> responseProjectionByPackage)
        {
            var normalizedHardwareIds = (hardwareIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var hardwareId in normalizedHardwareIds)
            {
                var candidates = ReadDriverCandidates(connection, hardwareId);
                if (candidates.Count == 0)
                {
                    continue;
                }

                var matched = MatchDriverCandidates(candidates, computerHardwareIds);
                if (matched == null)
                {
                    continue;
                }

                var driver = LoadPackageByIndex(
                    connection,
                    matched.Candidate.PackageIndex) as DriverUpdate;
                if (driver == null)
                {
                    continue;
                }

                if (!responseProjectionByPackage.TryGetValue(
                    matched.Candidate.PackageIndex,
                    out var responseProjection))
                {
                    responseProjection = ReadClientSyncResponseProjection(
                        connection,
                        matched.Candidate.PackageIndex);
                    if (responseProjection == null)
                    {
                        continue;
                    }

                    responseProjectionByPackage.Add(
                        matched.Candidate.PackageIndex,
                        responseProjection);
                }

                var matchResult = new DriverMatchResult(driver)
                {
                    MatchedHardwareId = hardwareId,
                    MatchedVersion = matched.Candidate.Version,
                    MatchedFeatureScore = matched.FeatureScore,
                    MatchedComputerHardwareId = matched.ComputerHardwareId
                };
                return new ClientSyncDriverMatchRecord(
                    matchResult,
                    responseProjection.RevisionId,
                    responseProjection.CoreXml);
            }

            return null;
        }

        public IReadOnlyList<ClientSyncFileLocationRecord> GetFileLocations(
            IEnumerable<byte[]> sha1Digests)
        {
            ThrowIfDisposed();
            var requested = (sha1Digests ?? Array.Empty<byte[]>())
                .Where(value => value != null && value.Length > 0)
                .Select(value => new
                {
                    Bytes = value,
                    Base64 = Convert.ToBase64String(value)
                })
                .GroupBy(value => value.Base64, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            if (requested.Count == 0)
            {
                return Array.Empty<ClientSyncFileLocationRecord>();
            }

            var results = new List<ClientSyncFileLocationRecord>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT fl.mu_url
FROM file_locations AS fl
WHERE fl.sha1_base64 = $sha1
  AND fl.mu_url IS NOT NULL
  AND EXISTS (
      SELECT 1
      FROM package_file_map AS pfm
      JOIN packages AS p ON p.package_index = pfm.package_index
      WHERE pfm.sha1_base64 = fl.sha1_base64
        AND p.published = 1
  )
LIMIT 1;";
            var parameter = command.Parameters.Add("$sha1", SqliteType.Text);
            command.Prepare();

            foreach (var digest in requested)
            {
                parameter.Value = digest.Base64;
                var value = command.ExecuteScalar();
                if (value != null && value != DBNull.Value && !string.IsNullOrWhiteSpace((string)value))
                {
                    results.Add(new ClientSyncFileLocationRecord(digest.Bytes, (string)value));
                }
            }

            return results;
        }

        public void RecordObservedInventory(ObservedInventoryBatch observations)
        {
            ThrowIfDisposed();
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

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            UpsertObservedDetectoids(connection, transaction, detectoids, observedAt);
            UpsertObservedStringIdentifiers(
                connection,
                transaction,
                "observed_pnp_hardware_ids",
                "hardware_id",
                pnpHardwareIds,
                observedAt);
            UpsertObservedStringIdentifiers(
                connection,
                transaction,
                "observed_compatible_ids",
                "compatible_id",
                compatibleIds,
                observedAt);
            UpsertObservedStringIdentifiers(
                connection,
                transaction,
                "observed_computer_ids",
                "computer_id",
                computerIds,
                observedAt);
            transaction.Commit();
        }

        public void Dispose()
        {
            lock (MetadataCacheLock)
            {
                MetadataCache.Clear();
                MetadataCacheLru.Clear();
                MetadataCacheBytes = 0;
            }
            IsDisposed = true;
        }

        private ClientSyncPackageRecord ReadPackageRecord(
            SqliteConnection connection,
            string predicate,
            object value,
            bool parameterIsIdentity)
        {
            EnsureMetadataCacheGeneration(connection);
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT
    p.package_index,
    COALESCE(graph.is_bundle, 0),
    COALESCE(graph.is_bundled, 0)
FROM packages AS p
LEFT JOIN client_sync_graph AS graph
  ON graph.package_index = p.package_index
WHERE {predicate}
  AND p.published = 1
LIMIT 1;";
            if (parameterIsIdentity)
            {
                command.Parameters.AddWithValue("$value", (string)value);
            }
            else
            {
                command.Parameters.AddWithValue("$value", Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }

            int packageIndex;
            bool isBundle;
            bool isBundled;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                packageIndex = reader.GetInt32(0);
                isBundle = reader.GetInt64(1) != 0;
                isBundled = reader.GetInt64(2) != 0;
            }

            var materialized = LoadPackageByIndex(connection, packageIndex);
            return materialized == null
                ? null
                : new ClientSyncPackageRecord(
                    packageIndex,
                    materialized,
                    isBundle,
                    isBundled);
        }

        private static ClientSyncResponseProjection ReadClientSyncResponseProjection(
            SqliteConnection connection,
            int packageIndex)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT fragment.core_xml
FROM client_sync_fragments AS fragment
JOIN packages AS package
  ON package.package_index = fragment.package_index
 AND package.published = 1
WHERE fragment.package_index = $packageIndex
LIMIT 1;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value
                ? null
                : new ClientSyncResponseProjection(packageIndex, (string)value);
        }

        private static string BuildSoftwareCandidateQuery(ClientSyncSoftwareStage stage, bool includeCoreXml)
        {
            var stagePredicate = stage switch
            {
                ClientSyncSoftwareStage.Root => @"
graph.package_type < $driverType
AND graph.has_prerequisites = 0
AND graph.has_dependents = 1",
                ClientSyncSoftwareStage.NonLeaf => @"
graph.package_type < $driverType
AND graph.has_prerequisites = 1
AND graph.has_dependents = 1",
                ClientSyncSoftwareStage.BundledLeaf => @"
graph.package_type = $softwareType
AND graph.has_prerequisites = 1
AND graph.has_dependents = 0
AND graph.is_superseded = 0
AND graph.is_bundled = 1",
                ClientSyncSoftwareStage.Leaf => @"
graph.package_type = $softwareType
AND graph.has_prerequisites = 1
AND graph.has_dependents = 0
AND graph.is_superseded = 0
AND graph.is_bundled = 0",
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            };

            var applicabilityPredicate = stage == ClientSyncSoftwareStage.Root
                ? string.Empty
                : @"
AND NOT EXISTS (
    SELECT 1
    FROM client_sync_prerequisites AS prerequisite
    LEFT JOIN temp_client_sync_installed AS installed
      ON installed.value = prerequisite.prerequisite_update_id
    WHERE prerequisite.package_index = graph.package_index
    GROUP BY prerequisite.group_ordinal
    HAVING COUNT(installed.value) = 0
)";

            var coreProjection = includeCoreXml ? ",\n    fragment.core_xml" : string.Empty;
            var fragmentJoin = includeCoreXml
                ? "JOIN client_sync_fragments AS fragment ON fragment.package_index = graph.package_index"
                : string.Empty;

            return $@"
SELECT
    graph.package_index,
    graph.is_bundle,
    graph.is_bundled{coreProjection}
FROM client_sync_graph AS graph
JOIN packages AS package
  ON package.package_index = graph.package_index
 AND package.published = 1
{fragmentJoin}
LEFT JOIN temp_client_sync_excluded AS excluded
  ON excluded.value = graph.update_id
LEFT JOIN temp_client_sync_approved AS approved
  ON approved.value = package.identity
WHERE excluded.value IS NULL
  AND {stagePredicate}
  AND (
      $approveAll = 1
      OR graph.package_type <> $softwareType
      OR approved.value IS NOT NULL
  )
  {applicabilityPredicate}
ORDER BY graph.package_index
LIMIT $limit;";
        }

        private List<DriverCandidateProjection> ReadDriverCandidates(
            SqliteConnection connection,
            string hardwareId)
        {
            var results = new List<DriverCandidateProjection>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT
    driver.package_index,
    driver.metadata_ordinal,
    driver.driver_date_ticks,
    driver.driver_version
FROM client_sync_driver_hardware_ids AS driver
JOIN client_sync_graph AS graph
  ON graph.package_index = driver.package_index
 AND graph.package_type = $driverType
WHERE driver.hardware_id = $hardwareId
  AND NOT EXISTS (
      SELECT 1
      FROM client_sync_prerequisites AS prerequisite
      LEFT JOIN temp_client_sync_installed AS installed
        ON installed.value = prerequisite.prerequisite_update_id
      WHERE prerequisite.package_index = graph.package_index
      GROUP BY prerequisite.group_ordinal
      HAVING COUNT(installed.value) = 0
  )
ORDER BY driver.package_index, driver.metadata_ordinal;";
                command.Parameters.AddWithValue("$driverType", (int)StoredPackageType.MicrosoftUpdateDriver);
                command.Parameters.AddWithValue("$hardwareId", hardwareId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var versionText = reader.GetString(3);
                    if (!ulong.TryParse(
                            versionText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var version))
                    {
                        version = 0;
                    }

                    results.Add(new DriverCandidateProjection(
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        new DriverVersion
                        {
                            Date = new DateTime(reader.GetInt64(2), DateTimeKind.Unspecified),
                            Version = version
                        }));
                }
            }

            if (results.Count == 0)
            {
                return results;
            }

            PopulateTemporaryDriverCandidateTable(connection, results);
            var byKey = results.ToDictionary(
                candidate => (candidate.PackageIndex, candidate.MetadataOrdinal));

            using (var computerCommand = connection.CreateCommand())
            {
                computerCommand.CommandText = @"
SELECT computer.package_index, computer.metadata_ordinal, computer.computer_id
FROM client_sync_driver_computer_ids AS computer
JOIN temp_client_sync_driver_candidates AS candidate
  ON candidate.package_index = computer.package_index
 AND candidate.metadata_ordinal = computer.metadata_ordinal
ORDER BY computer.package_index, computer.metadata_ordinal, computer.computer_id;";
                using var reader = computerCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (Guid.TryParse(reader.GetString(2), out var computerId)
                        && byKey.TryGetValue((reader.GetInt32(0), reader.GetInt32(1)), out var candidate))
                    {
                        candidate.ComputerHardwareIds.Add(computerId);
                    }
                }
            }

            using (var scoreCommand = connection.CreateCommand())
            {
                scoreCommand.CommandText = @"
SELECT score.package_index, score.metadata_ordinal, score.operating_system, score.score
FROM client_sync_driver_feature_scores AS score
JOIN temp_client_sync_driver_candidates AS candidate
  ON candidate.package_index = score.package_index
 AND candidate.metadata_ordinal = score.metadata_ordinal
ORDER BY score.package_index, score.metadata_ordinal, score.score, score.operating_system;";
                using var reader = scoreCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (byKey.TryGetValue((reader.GetInt32(0), reader.GetInt32(1)), out var candidate))
                    {
                        candidate.FeatureScores.Add(new DriverFeatureScore
                        {
                            OperatingSystem = reader.GetString(2),
                            Score = Convert.ToByte(reader.GetInt32(3), CultureInfo.InvariantCulture)
                        });
                    }
                }
            }

            return results;
        }

        private static DriverCandidateSelection MatchDriverCandidates(
            IReadOnlyCollection<DriverCandidateProjection> candidates,
            IReadOnlyList<Guid> computerHardwareIds)
        {
            var withComputerTarget = candidates
                .Where(candidate => candidate.ComputerHardwareIds.Count > 0)
                .ToList();

            foreach (var computerHardwareId in computerHardwareIds)
            {
                var computerMatches = withComputerTarget
                    .Where(candidate => candidate.ComputerHardwareIds.Contains(computerHardwareId))
                    .ToList();
                if (computerMatches.Count == 0)
                {
                    continue;
                }

                var matchesWithScores = computerMatches
                    .Where(candidate => candidate.FeatureScores.Count > 0)
                    .ToList();
                if (matchesWithScores.Count > 0)
                {
                    var bestScore = matchesWithScores
                        .SelectMany(candidate => candidate.FeatureScores)
                        .OrderBy(score => score.Score)
                        .First();
                    var selected = matchesWithScores.First(candidate =>
                        candidate.FeatureScores.Any(score => score.Score == bestScore.Score));
                    return new DriverCandidateSelection(
                        selected,
                        bestScore,
                        computerHardwareId);
                }

                var versionSelected = computerMatches
                    .OrderByDescending(candidate => candidate.Version.Date)
                    .ThenByDescending(candidate => candidate.Version.Version)
                    .First();
                return new DriverCandidateSelection(
                    versionSelected,
                    featureScore: null,
                    computerHardwareId: computerHardwareId);
            }

            var simpleSelected = candidates
                .Where(candidate => candidate.ComputerHardwareIds.Count == 0)
                .OrderByDescending(candidate => candidate.Version.Date)
                .ThenByDescending(candidate => candidate.Version.Version)
                .FirstOrDefault();
            return simpleSelected == null
                ? null
                : new DriverCandidateSelection(
                    simpleSelected,
                    featureScore: null,
                    computerHardwareId: null);
        }

        private static void PopulateTemporaryDriverCandidateTable(
            SqliteConnection connection,
            IReadOnlyCollection<DriverCandidateProjection> candidates)
        {
            using (var createCommand = connection.CreateCommand())
            {
                createCommand.CommandText = @"
CREATE TEMP TABLE IF NOT EXISTS temp_client_sync_driver_candidates (
    package_index INTEGER NOT NULL,
    metadata_ordinal INTEGER NOT NULL,
    PRIMARY KEY(package_index, metadata_ordinal)
) WITHOUT ROWID;
DELETE FROM temp_client_sync_driver_candidates;";
                createCommand.ExecuteNonQuery();
            }

            using var transaction = connection.BeginTransaction();
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = @"
INSERT OR IGNORE INTO temp_client_sync_driver_candidates(package_index, metadata_ordinal)
VALUES ($packageIndex, $metadataOrdinal);";
            var packageParameter = insertCommand.Parameters.Add("$packageIndex", SqliteType.Integer);
            var ordinalParameter = insertCommand.Parameters.Add("$metadataOrdinal", SqliteType.Integer);
            insertCommand.Prepare();
            foreach (var candidate in candidates)
            {
                packageParameter.Value = candidate.PackageIndex;
                ordinalParameter.Value = candidate.MetadataOrdinal;
                insertCommand.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        private MicrosoftUpdatePackage LoadPackageByIndex(
            SqliteConnection connection,
            int packageIndex)
        {
            var entry = GetMetadataCacheEntry(connection, packageIndex);
            if (entry == null)
            {
                return null;
            }

            lock (entry.SyncRoot)
            {
                if (!entry.PackageMaterialized)
                {
                    using var stream = new MemoryStream(entry.MetadataBytes, writable: false);
                    entry.Package = MicrosoftUpdatePackage.FromStoredMetadataXml(stream, null);
                    entry.PackageMaterialized = true;
                }

                return entry.Package;
            }
        }

        private MetadataCacheEntry GetMetadataCacheEntry(
            SqliteConnection connection,
            int packageIndex)
        {
            lock (MetadataCacheLock)
            {
                if (MetadataCache.TryGetValue(packageIndex, out var cachedNode))
                {
                    MetadataCacheLru.Remove(cachedNode);
                    MetadataCacheLru.AddFirst(cachedNode);
                    Interlocked.Increment(ref MetadataCacheHits);
                    return cachedNode.Value;
                }
            }

            Interlocked.Increment(ref MetadataCacheMisses);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT metadata, COALESCE(metadata_compression, 'none')
FROM packages
WHERE package_index = $packageIndex
  AND published = 1;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var decompressStopwatch = Stopwatch.StartNew();
            var bytes = DecompressBytes(
                (byte[])reader[0],
                reader.IsDBNull(1) ? CompressionNone : reader.GetString(1));
            decompressStopwatch.Stop();
            if (decompressStopwatch.ElapsedMilliseconds >= 100)
            {
                Trace.TraceInformation(
                    $"Decompressed metadata package_index={packageIndex} in " +
                    $"{decompressStopwatch.ElapsedMilliseconds} ms ({bytes?.Length ?? 0} bytes).");
            }

            var entry = new MetadataCacheEntry(packageIndex, bytes);
            lock (MetadataCacheLock)
            {
                if (MetadataCache.TryGetValue(packageIndex, out var concurrentNode))
                {
                    MetadataCacheLru.Remove(concurrentNode);
                    MetadataCacheLru.AddFirst(concurrentNode);
                    return concurrentNode.Value;
                }

                var node = MetadataCacheLru.AddFirst(entry);
                MetadataCache.Add(packageIndex, node);
                MetadataCacheBytes += entry.MetadataBytes.LongLength;
                while (MetadataCache.Count > MetadataCacheCapacity
                    || MetadataCacheBytes > MetadataCacheMaxBytes)
                {
                    var last = MetadataCacheLru.Last;
                    if (last == null)
                    {
                        break;
                    }

                    MetadataCache.Remove(last.Value.PackageIndex);
                    MetadataCacheBytes -= last.Value.MetadataBytes.LongLength;
                    MetadataCacheLru.RemoveLast();
                }
            }

            return entry;
        }

        private void EnsureMetadataCacheGeneration(SqliteConnection connection)
        {
            var generationText = ReadProperty(connection, "catalog_generation");
            if (!long.TryParse(
                    generationText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var generation))
            {
                generation = 0;
            }

            lock (MetadataCacheLock)
            {
                if (MetadataCacheGeneration == generation)
                {
                    return;
                }

                MetadataCache.Clear();
                MetadataCacheLru.Clear();
                MetadataCacheBytes = 0;
                MetadataCacheGeneration = generation;
                Trace.TraceInformation(
                    $"Client metadata cache reset for catalog generation {generation}.");
            }
        }

        private int GetPublishedPackageIndex(
            SqliteConnection connection,
            MicrosoftUpdatePackageIdentity identity)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT package_index
FROM packages
WHERE identity = $identity
  AND published = 1;";
            command.Parameters.AddWithValue("$identity", identity.ToString());
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value
                ? -1
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private byte[] ReadMetadataBytesByIdentity(
            SqliteConnection connection,
            MicrosoftUpdatePackageIdentity identity)
        {
            var packageIndex = GetPublishedPackageIndex(connection, identity);
            if (packageIndex < 0)
            {
                throw new KeyNotFoundException($"Published package not found: {identity}");
            }

            return ReadMetadataBytes(connection, packageIndex);
        }

        private byte[] ReadMetadataBytes(SqliteConnection connection, int packageIndex)
        {
            var entry = GetMetadataCacheEntry(connection, packageIndex);
            if (entry == null)
            {
                throw new KeyNotFoundException($"Published package not found: {packageIndex}");
            }

            return entry.MetadataBytes;
        }

        private static Dictionary<string, UpdateFileUrl> ReadPackageFileUrls(
            SqliteConnection connection,
            int packageIndex)
        {
            var urls = new Dictionary<string, UpdateFileUrl>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT fl.sha1_base64, fl.mu_url
FROM package_file_map AS pfm
JOIN file_locations AS fl ON fl.sha1_base64 = pfm.sha1_base64
JOIN packages AS p ON p.package_index = pfm.package_index
WHERE pfm.package_index = $packageIndex
  AND p.published = 1
  AND fl.mu_url IS NOT NULL;";
            command.Parameters.AddWithValue("$packageIndex", packageIndex);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var digest = reader.GetString(0);
                urls[digest] = new UpdateFileUrl(digest, reader.GetString(1), null);
            }

            return urls;
        }

        private IReadOnlyDictionary<int, MicrosoftUpdatePackageIdentity> ReadPackageIdentities(
            IEnumerable<int> revisionIds,
            bool observedDetectorsOnly)
        {
            ThrowIfDisposed();
            var requested = (revisionIds ?? Array.Empty<int>())
                .Distinct()
                .ToList();
            if (requested.Count == 0)
            {
                return new Dictionary<int, MicrosoftUpdatePackageIdentity>();
            }

            const int batchSize = 500;
            var result = new Dictionary<int, MicrosoftUpdatePackageIdentity>();
            using var connection = OpenConnection();
            for (var offset = 0; offset < requested.Count; offset += batchSize)
            {
                var batch = requested.Skip(offset).Take(batchSize).ToList();
                using var command = connection.CreateCommand();
                var parameterNames = new string[batch.Count];
                for (var index = 0; index < batch.Count; index++)
                {
                    parameterNames[index] = "$revision" + index.ToString(CultureInfo.InvariantCulture);
                    command.Parameters.AddWithValue(parameterNames[index], batch[index]);
                }

                command.CommandText = $@"
SELECT package_index, identity
FROM packages
WHERE published = 1
  {(observedDetectorsOnly
      ? "AND package_type IN ($detectoidType, $productType)"
      : string.Empty)}
  AND package_index IN ({string.Join(", ", parameterNames)});";
                if (observedDetectorsOnly)
                {
                    command.Parameters.AddWithValue(
                        "$detectoidType",
                        (int)StoredPackageType.MicrosoftUpdateDetectoid);
                    command.Parameters.AddWithValue(
                        "$productType",
                        (int)StoredPackageType.MicrosoftUpdateProduct);
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result[reader.GetInt32(0)] =
                        MicrosoftUpdatePackageIdentity.FromString(reader.GetString(1));
                }
            }

            return result;
        }

        private static void PopulateTemporaryGuidTable(
            SqliteConnection connection,
            string tableName,
            IEnumerable<Guid> values)
        {
            PopulateTemporaryTextTable(
                connection,
                tableName,
                (values ?? Array.Empty<Guid>())
                    .Where(value => value != Guid.Empty)
                    .Distinct()
                    .Select(value => value.ToString("D")));
        }

        private static void PopulateTemporaryTextTable(
            SqliteConnection connection,
            string tableName,
            IEnumerable<string> values)
        {
            var allowedTableNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "temp_client_sync_installed",
                "temp_client_sync_excluded",
                "temp_client_sync_approved"
            };
            if (!allowedTableNames.Contains(tableName))
            {
                throw new ArgumentOutOfRangeException(nameof(tableName));
            }

            using (var createCommand = connection.CreateCommand())
            {
                createCommand.CommandText = $@"
CREATE TEMP TABLE IF NOT EXISTS {tableName} (
    value TEXT PRIMARY KEY
) WITHOUT ROWID;
DELETE FROM {tableName};";
                createCommand.ExecuteNonQuery();
            }

            var normalizedValues = (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (normalizedValues.Count == 0)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = $@"
INSERT OR IGNORE INTO {tableName}(value)
VALUES ($value);";
            var valueParameter = insertCommand.Parameters.Add("$value", SqliteType.Text);
            insertCommand.Prepare();
            foreach (var value in normalizedValues)
            {
                valueParameter.Value = value;
                insertCommand.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        private SqliteConnection OpenConnection()
        {
            ThrowIfDisposed();
            var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
PRAGMA foreign_keys=ON;
PRAGMA busy_timeout=30000;";
                command.ExecuteNonQuery();
            }
            return connection;
        }

        private static string ReadProperty(SqliteConnection connection, string key)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM store_properties WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? null : (string)value;
        }

        private static byte[] DecompressBytes(byte[] value, string compression)
        {
            if (value == null
                || value.Length == 0
                || string.IsNullOrEmpty(compression)
                || string.Equals(compression, CompressionNone, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            using var input = new MemoryStream(value, writable: false);
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
                throw new InvalidDataException($"Unsupported metadata compression: {compression}");
            }

            return output.ToArray();
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

        private static void UpsertObservedDetectoids(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyCollection<ObservedDetectoidIdentity> detectoids,
            string observedAt)
        {
            if (detectoids.Count == 0)
            {
                return;
            }

            using var command = connection.CreateCommand();
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
            command.Parameters.AddWithValue("$observedAt", observedAt);
            command.Prepare();

            foreach (var detectoid in detectoids)
            {
                updateIdParameter.Value = detectoid.UpdateId.ToString("D");
                revisionParameter.Value = detectoid.RevisionNumber;
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertObservedStringIdentifiers(
            SqliteConnection connection,
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

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
INSERT INTO {tableName}({columnName}, first_seen, last_seen, observation_count)
VALUES ($identifier, $observedAt, $observedAt, 1)
ON CONFLICT({columnName}) DO UPDATE SET
    first_seen = MIN({tableName}.first_seen, excluded.first_seen),
    last_seen = MAX({tableName}.last_seen, excluded.last_seen),
    observation_count = {tableName}.observation_count + 1;";
            var identifierParameter = command.Parameters.Add("$identifier", SqliteType.Text);
            command.Parameters.AddWithValue("$observedAt", observedAt);
            command.Prepare();

            foreach (var identifier in identifiers)
            {
                identifierParameter.Value = identifier;
                command.ExecuteNonQuery();
            }
        }

        private static void ValidateSchema(SqliteConnection connection)
        {
            using (var propertiesCommand = connection.CreateCommand())
            {
                propertiesCommand.CommandText = @"
SELECT COUNT(*)
FROM sqlite_schema
WHERE type = 'table'
  AND name = 'store_properties';";
                if (Convert.ToInt32(
                        propertiesCommand.ExecuteScalar(),
                        CultureInfo.InvariantCulture) != 1)
                {
                    throw new InvalidDataException(
                        "Unsupported SQLite metadata store. Delete metadata.sqlite and run pre-fetch again.");
                }
            }

            var schemaVersionText = ReadProperty(connection, "schema_version");
            if (!int.TryParse(schemaVersionText, out var schemaVersion)
                || schemaVersion != RequiredSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported SQLite metadata schema '{schemaVersionText ?? "missing"}'. " +
                    $"Delete metadata.sqlite and run pre-fetch again with schema {RequiredSchemaVersion}.");
            }

            var requiredTables = new[]
            {
                "packages",
                "client_sync_packages",
                "client_sync_fragments",
                "client_sync_graph",
                "client_sync_prerequisites",
                "client_sync_supersedence",
                "client_sync_bundles",
                "client_sync_driver_hardware_ids",
                "client_sync_driver_computer_ids",
                "client_sync_driver_feature_scores",
                "file_locations",
                "package_file_map",
                "observed_detectoids",
                "observed_pnp_hardware_ids",
                "observed_compatible_ids",
                "observed_computer_ids"
            };

            using (var tableCommand = connection.CreateCommand())
            {
                tableCommand.CommandText = @"
SELECT COUNT(*)
FROM sqlite_schema
WHERE type = 'table'
  AND name = $tableName;";
                var tableParameter = tableCommand.Parameters.Add("$tableName", SqliteType.Text);
                tableCommand.Prepare();
                foreach (var tableName in requiredTables)
                {
                    tableParameter.Value = tableName;
                    if (Convert.ToInt32(
                            tableCommand.ExecuteScalar(),
                            CultureInfo.InvariantCulture) != 1)
                    {
                        throw new InvalidDataException(
                            $"SQLite metadata schema {RequiredSchemaVersion} is incomplete: " +
                            $"missing table '{tableName}'. Delete metadata.sqlite and run pre-fetch again.");
                    }
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*)
FROM pragma_table_info('packages')
WHERE name = 'published';";
            var publishedColumnCount = Convert.ToInt32(
                command.ExecuteScalar(),
                CultureInfo.InvariantCulture);
            if (publishedColumnCount != 1)
            {
                throw new InvalidDataException(
                    "The SQLite metadata store does not contain the direct client-sync publication column. " +
                    "Delete metadata.sqlite and run pre-fetch again.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(SQLiteClientSyncMetadataStore));
            }
        }

        private sealed class MetadataCacheEntry
        {
            public int PackageIndex { get; }
            public byte[] MetadataBytes { get; }
            public object SyncRoot { get; } = new();
            public bool PackageMaterialized { get; set; }
            public MicrosoftUpdatePackage Package { get; set; }

            public MetadataCacheEntry(int packageIndex, byte[] metadataBytes)
            {
                PackageIndex = packageIndex;
                MetadataBytes = metadataBytes ?? Array.Empty<byte>();
            }
        }

        private sealed class ClientSyncResponseProjection
        {
            public int RevisionId { get; }
            public string CoreXml { get; }

            public ClientSyncResponseProjection(int revisionId, string coreXml)
            {
                RevisionId = revisionId;
                CoreXml = coreXml ?? throw new ArgumentNullException(nameof(coreXml));
            }
        }

        private sealed class DriverCandidateProjection
        {
            public int PackageIndex { get; }
            public int MetadataOrdinal { get; }
            public DriverVersion Version { get; }
            public List<Guid> ComputerHardwareIds { get; } = new();
            public List<DriverFeatureScore> FeatureScores { get; } = new();

            public DriverCandidateProjection(
                int packageIndex,
                int metadataOrdinal,
                DriverVersion version)
            {
                PackageIndex = packageIndex;
                MetadataOrdinal = metadataOrdinal;
                Version = version ?? new DriverVersion();
            }
        }

        private sealed class DriverCandidateSelection
        {
            public DriverCandidateProjection Candidate { get; }
            public DriverFeatureScore FeatureScore { get; }
            public Guid? ComputerHardwareId { get; }

            public DriverCandidateSelection(
                DriverCandidateProjection candidate,
                DriverFeatureScore featureScore,
                Guid? computerHardwareId)
            {
                Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
                FeatureScore = featureScore;
                ComputerHardwareId = computerHardwareId;
            }
        }
    }
}
