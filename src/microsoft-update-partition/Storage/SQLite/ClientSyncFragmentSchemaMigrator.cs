// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Performs the one-time schema 9 to 10 migration without loading the full
    /// catalog into memory. Existing metadata remains intact if migration fails.
    /// </summary>
    internal static class ClientSyncFragmentSchemaMigrator
    {
        private const string CompressionNone = "none";
        private const string CompressionBrotli = "br";
        private const string CompressionGZip = "gzip";
        private const int SourceSchemaVersion = 9;
        private const int TargetSchemaVersion = 10;
        private const int MigrationBatchSize = 64;

        public static bool TryMigrate(SqliteConnection connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (!TableExists(connection, "store_properties")
                || !TableExists(connection, "packages")
                || !TableExists(connection, "client_sync_packages"))
            {
                return false;
            }

            var currentVersion = ReadSchemaVersion(connection);
            if (currentVersion != SourceSchemaVersion)
            {
                return false;
            }

            using var transaction = connection.BeginTransaction(deferred: false);

            // Re-check inside the transaction in case another process completed
            // the migration while this connection was waiting for the writer lock.
            currentVersion = ReadSchemaVersion(connection, transaction);
            if (currentVersion != SourceSchemaVersion)
            {
                transaction.Rollback();
                return false;
            }

            using (var createCommand = connection.CreateCommand())
            {
                createCommand.Transaction = transaction;
                createCommand.CommandText = @"
CREATE TABLE IF NOT EXISTS client_sync_fragments (
    package_index INTEGER PRIMARY KEY,
    core_xml TEXT NOT NULL,
    FOREIGN KEY(package_index) REFERENCES packages(package_index) ON DELETE CASCADE
);";
                createCommand.ExecuteNonQuery();
            }

            var lastPackageIndex = -1;
            while (true)
            {
                var batch = ReadMetadataBatch(
                    connection,
                    transaction,
                    lastPackageIndex,
                    MigrationBatchSize);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var row in batch)
                {
                    var metadata = DecompressBytes(row.Metadata, row.Compression);
                    var coreXml = ClientSyncCoreFragmentBuilder.Build(metadata);

                    using var insertCommand = connection.CreateCommand();
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = @"
INSERT INTO client_sync_fragments(package_index, core_xml)
VALUES ($packageIndex, $coreXml)
ON CONFLICT(package_index) DO UPDATE SET core_xml = excluded.core_xml;";
                    insertCommand.Parameters.AddWithValue("$packageIndex", row.PackageIndex);
                    insertCommand.Parameters.AddWithValue("$coreXml", coreXml);
                    insertCommand.ExecuteNonQuery();

                    lastPackageIndex = row.PackageIndex;
                }
            }

            using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = @"
UPDATE store_properties
SET value = $targetVersion
WHERE key = 'schema_version';";
                versionCommand.Parameters.AddWithValue(
                    "$targetVersion",
                    TargetSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (versionCommand.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("SQLite metadata store is missing schema_version.");
                }
            }

            transaction.Commit();
            return true;
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_schema
WHERE type = 'table'
  AND name = $tableName;";
            command.Parameters.AddWithValue("$tableName", tableName);
            return Convert.ToInt32(
                command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
        }

        private static int ReadSchemaVersion(
            SqliteConnection connection,
            SqliteTransaction transaction = null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT value
FROM store_properties
WHERE key = 'schema_version'
LIMIT 1;";
            var value = command.ExecuteScalar();
            return value != null
                && value != DBNull.Value
                && int.TryParse((string)value, out var version)
                    ? version
                    : -1;
        }

        private static List<MetadataRow> ReadMetadataBatch(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int lastPackageIndex,
            int batchSize)
        {
            var rows = new List<MetadataRow>(batchSize);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT
    package.package_index,
    package.metadata,
    package.metadata_compression
FROM packages AS package
JOIN client_sync_packages AS client_package
  ON client_package.package_index = package.package_index
WHERE package.package_index > $lastPackageIndex
ORDER BY package.package_index
LIMIT $batchSize;";
            command.Parameters.AddWithValue("$lastPackageIndex", lastPackageIndex);
            command.Parameters.AddWithValue("$batchSize", batchSize);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MetadataRow(
                    reader.GetInt32(0),
                    (byte[])reader.GetValue(1),
                    reader.IsDBNull(2) ? CompressionNone : reader.GetString(2)));
            }

            return rows;
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

        private sealed class MetadataRow
        {
            public int PackageIndex { get; }
            public byte[] Metadata { get; }
            public string Compression { get; }

            public MetadataRow(int packageIndex, byte[] metadata, string compression)
            {
                PackageIndex = packageIndex;
                Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
                Compression = compression ?? CompressionNone;
            }
        }
    }
}
