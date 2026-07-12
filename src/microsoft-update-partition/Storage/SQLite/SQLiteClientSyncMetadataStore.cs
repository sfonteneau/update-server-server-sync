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
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.XPath;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Client-facing SQLite read model. Every operation opens a short-lived SQLite
    /// connection and materializes only the rows needed by the current SOAP request.
    /// No package map, metadata graph, driver index or package object is retained.
    /// </summary>
    internal sealed class SQLiteClientSyncMetadataStore : IClientSyncMetadataStore
    {
        private const int RequiredSchemaVersion = 8;
        private const int MaxObservedValuesPerType = 20000;
        private const int MaxObservedIdentifierLength = 2048;
        private const string CompressionNone = "none";
        private const string CompressionBrotli = "br";
        private const string CompressionGZip = "gzip";

        private readonly string DatabasePath;
        private readonly string ConnectionString;
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
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using var connection = OpenConnection();
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

            var installed = new HashSet<Guid>(
                installedNonLeafUpdateIds ?? Array.Empty<Guid>());
            var installedList = installed.ToList();
            var excluded = new HashSet<Guid>(installed);
            excluded.UnionWith(otherCachedUpdateIds ?? Array.Empty<Guid>());
            var approved = new HashSet<MicrosoftUpdatePackageIdentity>(
                approvedSoftwareUpdates ?? Array.Empty<MicrosoftUpdatePackageIdentity>());

            var results = new List<ClientSyncPackageRecord>(maxResults + 1);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = BuildSoftwareCandidateQuery(stage);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(1), out var updateId)
                    || excluded.Contains(updateId))
                {
                    continue;
                }

                var package = MaterializePackage(
                    (byte[])reader[4],
                    reader.IsDBNull(5) ? CompressionNone : reader.GetString(5));
                if (package == null)
                {
                    continue;
                }

                if (stage != ClientSyncSoftwareStage.Root
                    && !package.IsApplicable(installedList))
                {
                    continue;
                }

                if (!approveAllSoftwareUpdates
                    && package is SoftwareUpdate
                    && !approved.Contains(package.Id))
                {
                    continue;
                }

                results.Add(new ClientSyncPackageRecord(
                    reader.GetInt32(0),
                    package,
                    reader.GetInt64(6) != 0,
                    reader.GetInt64(7) != 0));

                if (results.Count > maxResults)
                {
                    break;
                }
            }

            truncated = results.Count > maxResults;
            if (truncated)
            {
                results.RemoveAt(results.Count - 1);
            }

            return results;
        }

        public DriverMatchResult MatchDriver(
            IEnumerable<string> hardwareIds,
            IEnumerable<Guid> computerHardwareIds,
            IReadOnlyCollection<Guid> installedPrerequisites)
        {
            ThrowIfDisposed();
            var normalizedHardwareIds = (hardwareIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .ToList();
            var computerIds = (computerHardwareIds ?? Array.Empty<Guid>()).ToList();
            var installed = (installedPrerequisites ?? Array.Empty<Guid>()).ToList();

            using var connection = OpenConnection();
            foreach (var hardwareId in normalizedHardwareIds)
            {
                var candidates = ReadDriverCandidates(connection, hardwareId, installed);
                if (candidates.Count == 0)
                {
                    continue;
                }

                var matched = MatchDriverCandidates(candidates, computerIds);
                if (matched != null)
                {
                    matched.MatchedHardwareId = hardwareId;
                    return matched;
                }
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
            IsDisposed = true;
        }

        private ClientSyncPackageRecord ReadPackageRecord(
            SqliteConnection connection,
            string predicate,
            object value,
            bool parameterIsIdentity)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT
    p.package_index,
    p.metadata,
    COALESCE(p.metadata_compression, 'none'),
    EXISTS (
        SELECT 1
        FROM client_sync_bundles AS bundle
        WHERE bundle.bundle_package_index = p.package_index
    ) AS is_bundle,
    EXISTS (
        SELECT 1
        FROM client_sync_packages AS target
        JOIN client_sync_bundles AS bundle
          ON bundle.bundled_update_id = target.update_id
         AND bundle.bundled_revision_number = target.revision_number
        JOIN packages AS source_package
          ON source_package.package_index = bundle.bundle_package_index
         AND source_package.published = 1
        WHERE target.package_index = p.package_index
    ) AS is_bundled
FROM packages AS p
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

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var materialized = MaterializePackage(
                (byte[])reader[1],
                reader.IsDBNull(2) ? CompressionNone : reader.GetString(2));
            return materialized == null
                ? null
                : new ClientSyncPackageRecord(
                    reader.GetInt32(0),
                    materialized,
                    reader.GetInt64(3) != 0,
                    reader.GetInt64(4) != 0);
        }

        private static string BuildSoftwareCandidateQuery(ClientSyncSoftwareStage stage)
        {
            var stagePredicate = stage switch
            {
                ClientSyncSoftwareStage.Root => @"
graph.has_prerequisites = 0
AND graph.has_dependents = 1",
                ClientSyncSoftwareStage.NonLeaf => @"
graph.has_prerequisites = 1
AND graph.has_dependents = 1",
                ClientSyncSoftwareStage.BundledLeaf => $@"
p.package_type = {(int)StoredPackageType.MicrosoftUpdateSoftware}
AND graph.has_prerequisites = 1
AND graph.has_dependents = 0
AND NOT EXISTS (
    SELECT 1
    FROM client_sync_supersedence AS supersedence
    JOIN packages AS superseding_package
      ON superseding_package.package_index = supersedence.superseding_package_index
     AND superseding_package.published = 1
    WHERE supersedence.superseded_update_id = current.update_id
)
AND EXISTS (
    SELECT 1
    FROM client_sync_bundles AS bundle
    JOIN packages AS bundle_package
      ON bundle_package.package_index = bundle.bundle_package_index
     AND bundle_package.published = 1
    WHERE bundle.bundled_update_id = current.update_id
      AND bundle.bundled_revision_number = current.revision_number
)",
                ClientSyncSoftwareStage.Leaf => $@"
p.package_type = {(int)StoredPackageType.MicrosoftUpdateSoftware}
AND graph.has_prerequisites = 1
AND graph.has_dependents = 0
AND NOT EXISTS (
    SELECT 1
    FROM client_sync_supersedence AS supersedence
    JOIN packages AS superseding_package
      ON superseding_package.package_index = supersedence.superseding_package_index
     AND superseding_package.published = 1
    WHERE supersedence.superseded_update_id = current.update_id
)
AND NOT EXISTS (
    SELECT 1
    FROM client_sync_bundles AS bundle
    JOIN packages AS bundle_package
      ON bundle_package.package_index = bundle.bundle_package_index
     AND bundle_package.published = 1
    WHERE bundle.bundled_update_id = current.update_id
      AND bundle.bundled_revision_number = current.revision_number
)",
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            };

            // PrerequisitesGraph in the original implementation merged graph edges by
            // UpdateID across every revision. Reproduce that behavior in SQL while
            // materializing only the latest published revision for the response.
            return $@"
WITH latest AS (
    SELECT sync_package.update_id, MAX(sync_package.revision_number) AS revision_number
    FROM client_sync_packages AS sync_package
    JOIN packages AS published_package
      ON published_package.package_index = sync_package.package_index
     AND published_package.published = 1
    GROUP BY sync_package.update_id
),
graph AS (
    SELECT
        latest.update_id,
        EXISTS (
            SELECT 1
            FROM client_sync_packages AS own_revision
            JOIN packages AS own_package
              ON own_package.package_index = own_revision.package_index
             AND own_package.published = 1
            JOIN client_sync_prerequisites AS own_prerequisite
              ON own_prerequisite.package_index = own_revision.package_index
            WHERE own_revision.update_id = latest.update_id
        ) AS has_prerequisites,
        EXISTS (
            SELECT 1
            FROM client_sync_prerequisites AS dependent_prerequisite
            JOIN packages AS dependent_package
              ON dependent_package.package_index = dependent_prerequisite.package_index
             AND dependent_package.published = 1
            WHERE dependent_prerequisite.prerequisite_update_id = latest.update_id
        ) AS has_dependents
    FROM latest
)
SELECT
    p.package_index,
    current.update_id,
    current.revision_number,
    p.identity,
    p.metadata,
    COALESCE(p.metadata_compression, 'none'),
    EXISTS (
        SELECT 1
        FROM client_sync_bundles AS source_bundle
        WHERE source_bundle.bundle_package_index = p.package_index
    ) AS is_bundle,
    EXISTS (
        SELECT 1
        FROM client_sync_bundles AS containing_bundle
        JOIN packages AS containing_package
          ON containing_package.package_index = containing_bundle.bundle_package_index
         AND containing_package.published = 1
        WHERE containing_bundle.bundled_update_id = current.update_id
          AND containing_bundle.bundled_revision_number = current.revision_number
    ) AS is_bundled
FROM client_sync_packages AS current
JOIN latest
  ON latest.update_id = current.update_id
 AND latest.revision_number = current.revision_number
JOIN graph
  ON graph.update_id = current.update_id
JOIN packages AS p
  ON p.package_index = current.package_index
 AND p.published = 1
WHERE {stagePredicate}
ORDER BY p.package_index;";
        }

        private List<DriverCandidate> ReadDriverCandidates(
            SqliteConnection connection,
            string hardwareId,
            IReadOnlyCollection<Guid> installedPrerequisites)
        {
            var rows = new List<(int PackageIndex, int MetadataOrdinal)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT driver.package_index, driver.metadata_ordinal
FROM client_sync_driver_hardware_ids AS driver
JOIN packages AS package
  ON package.package_index = driver.package_index
 AND package.published = 1
WHERE driver.hardware_id = $hardwareId
ORDER BY driver.package_index, driver.metadata_ordinal;";
                command.Parameters.AddWithValue("$hardwareId", hardwareId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
                }
            }

            var installed = installedPrerequisites?.ToList() ?? new List<Guid>();
            var packages = new Dictionary<int, DriverUpdate>();
            var results = new List<DriverCandidate>();
            foreach (var row in rows)
            {
                if (!packages.TryGetValue(row.PackageIndex, out var driver))
                {
                    driver = LoadPackageByIndex(connection, row.PackageIndex) as DriverUpdate;
                    packages[row.PackageIndex] = driver;
                }

                if (driver == null || !driver.IsApplicable(installed))
                {
                    continue;
                }

                var metadata = driver.GetDriverMetadata();
                if (metadata == null
                    || row.MetadataOrdinal < 0
                    || row.MetadataOrdinal >= metadata.Count)
                {
                    continue;
                }

                results.Add(new DriverCandidate(driver, metadata[row.MetadataOrdinal]));
            }

            return results;
        }

        private static DriverMatchResult MatchDriverCandidates(
            IReadOnlyCollection<DriverCandidate> candidates,
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
                    .Where(candidate => candidate.Metadata.FeatureScores != null
                        && candidate.Metadata.FeatureScores.Count > 0)
                    .ToList();
                if (matchesWithScores.Count > 0)
                {
                    var bestScore = matchesWithScores
                        .SelectMany(candidate => candidate.Metadata.FeatureScores)
                        .OrderBy(score => score.Score)
                        .First();
                    var selected = matchesWithScores.First(candidate =>
                        candidate.Metadata.FeatureScores.Any(score => score.Score == bestScore.Score));
                    return new DriverMatchResult(selected.Driver)
                    {
                        MatchedVersion = selected.Metadata.Versioning,
                        MatchedFeatureScore = bestScore,
                        MatchedComputerHardwareId = computerHardwareId
                    };
                }

                var versionSelected = computerMatches
                    .OrderByDescending(candidate => candidate.Metadata.Versioning.Date)
                    .ThenByDescending(candidate => candidate.Metadata.Versioning.Version)
                    .First();
                return new DriverMatchResult(versionSelected.Driver)
                {
                    MatchedVersion = versionSelected.Metadata.Versioning,
                    MatchedComputerHardwareId = computerHardwareId
                };
            }

            var simpleCandidates = candidates
                .Where(candidate => candidate.ComputerHardwareIds.Count == 0)
                .OrderByDescending(candidate => candidate.Metadata.Versioning.Date)
                .ThenByDescending(candidate => candidate.Metadata.Versioning.Version)
                .ToList();
            if (simpleCandidates.Count == 0)
            {
                return null;
            }

            var simpleSelected = simpleCandidates[0];
            return new DriverMatchResult(simpleSelected.Driver)
            {
                MatchedVersion = simpleSelected.Metadata.Versioning
            };
        }

        private MicrosoftUpdatePackage LoadPackageByIndex(
            SqliteConnection connection,
            int packageIndex)
        {
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

            return MaterializePackage(
                (byte[])reader[0],
                reader.IsDBNull(1) ? CompressionNone : reader.GetString(1));
        }

        private static MicrosoftUpdatePackage MaterializePackage(
            byte[] metadata,
            string compression)
        {
            if (metadata == null || metadata.Length == 0)
            {
                return null;
            }

            var bytes = DecompressBytes(metadata, compression);
            using var stream = new MemoryStream(bytes, writable: false);
            return MicrosoftUpdatePackage.FromStoredMetadataXml(stream, null);
        }

        private static IReadOnlyList<Guid> GetEffectiveComputerHardwareIds(
            DriverMetadata metadata)
        {
            var target = metadata.TargetComputerHardwareId ?? new List<Guid>();
            var distribution = metadata.DistributionComputerHardwareId ?? new List<Guid>();
            if (target.Count > 0 && distribution.Count > 0)
            {
                return target.Intersect(distribution).ToList();
            }

            if (target.Count > 0)
            {
                return target;
            }

            if (distribution.Count > 0)
            {
                return distribution;
            }

            return Array.Empty<Guid>();
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
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT metadata, COALESCE(metadata_compression, 'none')
FROM packages
WHERE identity = $identity
  AND published = 1;";
            command.Parameters.AddWithValue("$identity", identity.ToString());
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"Published package not found: {identity}");
            }

            return DecompressBytes(
                (byte[])reader[0],
                reader.IsDBNull(1) ? CompressionNone : reader.GetString(1));
        }

        private byte[] ReadMetadataBytes(SqliteConnection connection, int packageIndex)
        {
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
                throw new KeyNotFoundException($"Published package not found: {packageIndex}");
            }

            return DecompressBytes(
                (byte[])reader[0],
                reader.IsDBNull(1) ? CompressionNone : reader.GetString(1));
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
      ? "AND package_type = $detectoidType"
      : string.Empty)}
  AND package_index IN ({string.Join(", ", parameterNames)});";
                if (observedDetectorsOnly)
                {
                    command.Parameters.AddWithValue(
                        "$detectoidType",
                        (int)StoredPackageType.MicrosoftUpdateDetectoid);
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

        private sealed class DriverCandidate
        {
            public DriverUpdate Driver { get; }
            public DriverMetadata Metadata { get; }
            public IReadOnlyList<Guid> ComputerHardwareIds { get; }

            public DriverCandidate(DriverUpdate driver, DriverMetadata metadata)
            {
                Driver = driver;
                Metadata = metadata;
                ComputerHardwareIds = GetEffectiveComputerHardwareIds(metadata);
            }
        }
    }
}
