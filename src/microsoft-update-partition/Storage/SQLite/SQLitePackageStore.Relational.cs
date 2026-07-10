// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate;
using Microsoft.PackageGraph.MicrosoftUpdate.Index;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Drivers;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage.Index;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Microsoft.PackageGraph.Storage.Local
{
    partial class SQLitePackageStore
    {
        private static readonly XNamespace UpdateNamespace = "http://schemas.microsoft.com/msus/2002/12/Update";
        private static readonly XNamespace DriverNamespace = "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/WindowsDriver";

        private void InitializeRelationalSchema()
        {
            EnsureColumnExists("packages", "storage_model", "TEXT NOT NULL DEFAULT 'legacy-xml'");
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

        private bool IsRelationalPackageIndex(int packageIndex)
        {
            return _IndexToIdentityMap.ContainsKey(packageIndex);
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

        private bool HasLegacyPackages()
        {
            return _RelationalPackageIndexes.Count != _IdentityToIndexMap.Count;
        }

        private int LegacyPackageCount()
        {
            return _IdentityToIndexMap.Count - _RelationalPackageIndexes.Count;
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

        private void DeleteRelationalRecord(int packageIndex, SqliteTransaction transaction)
        {
            var statements = new[]
            {
                "DELETE FROM package_file_map WHERE package_index = $packageIndex;",
                "DELETE FROM package_property_attributes WHERE package_index = $packageIndex;",
                "DELETE FROM package_property_elements WHERE package_index = $packageIndex;",
                "DELETE FROM package_localized_elements WHERE package_index = $packageIndex;",
                "DELETE FROM package_fragments WHERE package_index = $packageIndex;",
                "DELETE FROM package_relationship_extra_elements WHERE package_index = $packageIndex;",
                "DELETE FROM package_relationship_groups WHERE package_index = $packageIndex;",
                "DELETE FROM package_driver_metadata WHERE package_index = $packageIndex;"
            };

            foreach (var statement in statements)
            {
                using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                command.Parameters.AddWithValue("$packageIndex", packageIndex);
                command.ExecuteNonQuery();
            }
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
}
