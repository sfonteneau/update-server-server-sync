// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using CommandLine;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    enum PackageType
    {
        MicrosoftUpdateClassification,
        MicrosoftUpdateProduct,
        MicrosoftUpdateDetectoid,
        MicrosoftUpdateUpdate,
        MicrosoftUpdateDriver,
        AnyPackage
    }
    public interface IMetadataSourceOptions
    {
        string UpstreamEndpoint { get; }
        string EndpointType { get; }
    }

    public interface IIncrementalSyncOptions
    {
        bool IgnoreSyncAnchor { get; }

        bool ResetSyncCheckpoint { get; }

        int MetadataBatchSize { get; }
    }

    public interface IMetadataStoreOptions
    {
        string Alias { get; }

        string Path { get; }

        string Type { get; }

        string StoreConnectionString { get; }
    }

    public interface IMetadataFilterOptions
    {
        IEnumerable<string> ProductsFilter { get; }

        IEnumerable<string> ClassificationsFilter { get; }

        IEnumerable<string> IdFilter { get; }

        string HardwareIdFilter { get; }

        string ComputerHardwareIdFilter { get; set; }

        string TitleFilter { get; }

        bool SkipSuperseded { get; }

        IEnumerable<string> KbArticleFilter { get; }

        int FirstX { get; }
    }

    public interface IUpstreamTimeoutOptions
    {
        int UpstreamTimeoutMinutes { get; }
    }

    public interface ISyncQueryFilter
    {
        IEnumerable<string> ProductsFilter { get; }

        IEnumerable<string> ClassificationsFilter { get; }
    }

    [Verb("fetch-config", HelpText = "Retrieves upstream server configuration")]
    public class FetchConfigurationOptions : IUpstreamTimeoutOptions
    {
        [Option("endpoint", Required = false, HelpText = "The endpoint from which to fetch updates", SetName = "custom")]
        public string UpstreamEndpoint { get; set; }

        [Option("upstream-timeout-minutes", Required = false, Default = 3, HelpText = "SOAP timeout in minutes for requests to the upstream update server. Must be greater than zero.")]
        public int UpstreamTimeoutMinutes { get; set; }

        [Option("master", Required = false, Default = false, HelpText = "Only fetch categories", SetName = "official")]
        public bool MasterEndpoint { get; set; }

        [Option("destination", Required = true, HelpText = "Destination JSON file.")]
        public string OutFile { get; set; }
    }

    [Verb("pre-fetch", HelpText = "Retrieves metadata from an upstream server")]
    public class FetchCategoriesOptions : IMetadataStoreOptions, IUpstreamTimeoutOptions
    {
        [Option("endpoint", Required = false, HelpText = "The endpoint from which to fetch categories.", SetName = "custom")]
        public string UpstreamEndpoint { get; set; }

        [Option("upstream-timeout-minutes", Required = false, Default = 3, HelpText = "SOAP timeout in minutes for requests to the upstream update server. Must be greater than zero.")]
        public int UpstreamTimeoutMinutes { get; set; }

        [Option("master", Required = false, Default = false, HelpText = "Fetch categories from the official Microsoft upstream server.", SetName = "official")]
        public bool MasterEndpoint { get; set; }

        [Option("account-name", Required = false, HelpText = "Account name; if not set, a random GUID is used.")]
        public string AccountName { get; set; }

        [Option("account-guid", Required = false, HelpText = "Account GUID. If not set, a random GUID is used.")]
        public string AccountGuid { get; set; }

        [Option("store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Destination store")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }
    }

    [Verb("index", HelpText = "Indexes a package store")]
    public class ReindexStoreOptions : IMetadataStoreOptions
    {
        [Option("store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Store to index")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }

        [Option("force", Required = false, Default = false, HelpText = "Force indexing even when not required")]
        public bool ForceReindex { get; set; }
    }

    [Verb("compact-store", HelpText = "Compresses and compacts a local SQLite metadata store")]
    public class CompactStoreOptions : IMetadataStoreOptions
    {
        [Option("store-alias", Required = false, HelpText = "Store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Local store directory to compact")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; only local is supported for compaction")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Unused for local compaction")]
        public string StoreConnectionString { get; set; }

        [Option("replace", Required = false, Default = true, HelpText = "Replace metadata.sqlite with a VACUUM INTO compact copy and keep the previous file as backup")]
        public bool ReplaceDatabaseFile { get; set; }

        [Option("reindex", Required = false, Default = true, HelpText = "Rebuild indexes after compaction. Recommended to remove the old file-location index payload")]
        public bool RebuildIndexes { get; set; }
    }

    [Verb("fetch", HelpText = "Retrieves metadata from an upstream server")]
    public class FetchPackagesOptions : IMetadataStoreOptions, IMetadataSourceOptions, IIncrementalSyncOptions, IUpstreamTimeoutOptions
    {
        public const string MicrosoftUpdateEndpoint = "microsoft-update";
        public const string NuGetV3Endpoint = "nuget";
        public const string LinuxEndpoint = "linux";
        public const string WebEndpoint = "web";

        [Option("store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Destination store")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }

        [Option("endpoint", Required = false, HelpText = "The endpoint from which to fetch updates.")]
        public string UpstreamEndpoint { get; set; }

        [Option("endpoint-type", Required = false, Default = MicrosoftUpdateEndpoint, HelpText = "The endpoint from which to fetch updates.")]
        public string EndpointType { get; set; }

        [Option("upstream-timeout-minutes", Required = false, Default = 3, HelpText = "SOAP timeout in minutes for requests to the upstream update server. Must be greater than zero.")]
        public int UpstreamTimeoutMinutes { get; set; }

        [Option("product-filter", Required = false, Separator = '+', HelpText = "Product filter for sync'ing updates. Strongly recommended; without it Microsoft Update may return a very large catalog.")]
        public IEnumerable<string> ProductsFilter { get; set; }

        [Option("allow-all-products", Required = false, Default = false, HelpText = "Allow fetch without --product-filter. This can be extremely slow and large.")]
        public bool AllowAllProducts { get; set; }

        [Option("classification-filter", Required = false, Separator = '+', HelpText = "Classification filter for sync'ing updates")]
        public IEnumerable<string> ClassificationsFilter { get; set; }

        [Option("language-filter", Required = false, Separator = '+', HelpText = "Language LCIDs or short names to sync. Default: 1033/en. Use all to disable the language filter")]
        public IEnumerable<string> LanguageFilter { get; set; }

        [Option("refresh-categories", Required = false, Default = false, HelpText = "Refresh upstream categories before fetching updates. By default categories are fetched only when missing.")]
        public bool RefreshCategories { get; set; }

        [Option("ignore-sync-anchor", Required = false, Default = false, HelpText = "Ignore the saved upstream WSUS anchor and force a full revision list query. Any unfinished local checkpoint for this filter is discarded.")]
        public bool IgnoreSyncAnchor { get; set; }

        [Option("reset-sync-checkpoint", Required = false, Default = false, HelpText = "Discard an unfinished local fetch checkpoint for this exact endpoint/filter before starting.")]
        public bool ResetSyncCheckpoint { get; set; }

        [Option("metadata-batch-size", Required = false, Default = 100, HelpText = "Number of update identities retrieved and stored per local checkpoint batch.")]
        public int MetadataBatchSize { get; set; }

        [Option("keep-all-localized-properties", Required = false, Default = false, HelpText = "Do not strip non-requested LocalizedProperties from stored metadata")]
        public bool KeepAllLocalizedProperties { get; set; }

        [Option("account-name", Required = false, HelpText = "Account name; if not set, a random GUID is used.")]
        public string AccountName { get; set; }

        [Option("account-guid", Required = false, HelpText = "Account GUID. If not set, a random GUID is used.")]
        public string AccountGuid { get; set; }

        [Option("ids", Required = false, Separator = '+', HelpText = "Try fetch metadata for this list of ids (GUIDs)")]
        public IEnumerable<string> Ids { get; set; }
    }

    [Verb("fetch-observed", HelpText = "Fetches product updates and drivers observed during Windows Update client scans")]
    public class FetchObservedOptions : IMetadataStoreOptions, IMetadataSourceOptions, IIncrementalSyncOptions, IUpstreamTimeoutOptions
    {
        [Option("store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Destination store containing observed inventory")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; observed fetch currently requires a local SQLite store")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Unused for local observed product fetch")]
        public string StoreConnectionString { get; set; }

        [Option("endpoint", Required = false, HelpText = "The Microsoft Update endpoint from which to fetch updates")]
        public string UpstreamEndpoint { get; set; }

        [Option("endpoint-type", Required = false, Default = FetchPackagesOptions.MicrosoftUpdateEndpoint, HelpText = "Upstream endpoint type; only microsoft-update is supported")]
        public string EndpointType { get; set; }

        [Option("upstream-timeout-minutes", Required = false, Default = 3, HelpText = "SOAP timeout in minutes for requests to the upstream update server. Must be greater than zero.")]
        public int UpstreamTimeoutMinutes { get; set; }

        [Option("classification-filter", Required = false, Separator = '+', HelpText = "Classification GUIDs to fetch for each observed product; required unless --skip-products is used")]
        public IEnumerable<string> ClassificationsFilter { get; set; }

        [Option("language-filter", Required = false, Separator = '+', HelpText = "Language LCIDs or short names to sync. Default: 1033/en. Use all to disable the language filter")]
        public IEnumerable<string> LanguageFilter { get; set; }

        [Option("seen-within-days", Required = false, Default = 30, HelpText = "Use product and hardware observations seen within this many days; use 0 for all observations")]
        public int SeenWithinDays { get; set; }

        [Option("skip-products", Required = false, Default = false, HelpText = "Do not fetch software updates for observed products")]
        public bool SkipProducts { get; set; }

        [Option("skip-drivers", Required = false, Default = false, HelpText = "Do not fetch drivers for observed hardware identifiers")]
        public bool SkipDrivers { get; set; }

        [Option("include-compatible-ids", Required = false, Default = false, HelpText = "Include generic Compatible IDs in driver queries. Disabled by default because it can greatly broaden matches")]
        public bool IncludeCompatibleIds { get; set; }

        [Option("refresh-categories", Required = false, Default = false, HelpText = "Refresh categories/detectoids before resolving observed products")]
        public bool RefreshCategories { get; set; }

        [Option("rebuild-product-map", Required = false, Default = false, HelpText = "Rebuild the cached detectoid-to-product map before fetching")]
        public bool RebuildProductMap { get; set; }

        [Option("ignore-sync-anchor", Required = false, Default = false, HelpText = "Ignore saved product and per-identifier driver anchors and request full revision lists")]
        public bool IgnoreSyncAnchor { get; set; }

        [Option("reset-sync-checkpoint", Required = false, Default = false, HelpText = "Discard unfinished product and driver checkpoints for the selected scopes before starting")]
        public bool ResetSyncCheckpoint { get; set; }

        [Option("metadata-batch-size", Required = false, Default = 100, HelpText = "Number of update identities retrieved and stored per local checkpoint batch")]
        public int MetadataBatchSize { get; set; }

        [Option("keep-all-localized-properties", Required = false, Default = false, HelpText = "Do not strip non-requested LocalizedProperties from stored metadata")]
        public bool KeepAllLocalizedProperties { get; set; }

        [Option("dry-run", Required = false, Default = false, HelpText = "Display observed product and driver scopes without contacting Microsoft Update")]
        public bool DryRun { get; set; }
    }

    [Verb("fetch-content", HelpText = "Downloads update content from an upstream server")]
    public class ContentSyncOptions : IMetadataStoreOptions, IMetadataFilterOptions
    {
        [Option("metadata-store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("metadata-store-path", Required = false, HelpText = "Destination store")]
        public string Path { get; set; }

        [Option("metadata-store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }

        [Option("content-store-path", Required = true, HelpText = "Destination content store")]
        public string ContentPath { get; set; }

        [Option("content-store-type", Required = false, Default = "local", HelpText = "Content store type; default is local")]
        public string ContentStoreType { get; set; }

        [Option("content-connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string ContentStoreConnectionString { get; set; }

        [Option("product-filter", Required = false, Separator = '+', HelpText = "Product filter for sync'ing updates")]
        public IEnumerable<string> ProductsFilter { get; set; }

        [Option("classification-filter", Required = false, Separator = '+', HelpText = "Classification filter for sync'ing updates")]
        public IEnumerable<string> ClassificationsFilter { get; set; }

        [Option("id-filter", Required = false, Separator = '+', HelpText = "ID filter")]
        public IEnumerable<string> IdFilter { get; set; }

        [Option("title-filter", Required = false, HelpText = "Title filter")]
        public string TitleFilter { get; set; }

        [Option("hwid-filter", Required = false, HelpText = "Hardware ID filter")]
        public string HardwareIdFilter { get; set; }

        [Option("computer-hwid-filter", Required = false, HelpText = "Computer hardware ID filter")]
        public string ComputerHardwareIdFilter { get; set; }

        [Option("kbarticle-filter", Required = false, Separator = '+', HelpText = "KB article filter (numbers only)")]
        public IEnumerable<string> KbArticleFilter { get; set; }

        [Option("skip-superseded", Required = false, Default = false, HelpText = "Do not consider superseded updates for download")]
        public bool SkipSuperseded { get; set; }

        [Option("first", Required = false, Default = 0, HelpText = "Content sync only the first x packages")]
        public int FirstX { get; set; }
    }

    [Verb("status", HelpText = "Displays status information about and updates metadata source")]
    public class MetadataSourceStatusOptions : IMetadataStoreOptions
    {
        [Option("store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Store to get status for")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }
    }


    [Verb("observed-status", HelpText = "Displays product detectoids and hardware identifiers observed during Windows Update client scans")]
    public class ObservedInventoryStatusOptions : IMetadataStoreOptions
    {
        [Option("store-alias", Required = false, HelpText = "Store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Store containing observed inventory")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; observed inventory currently requires a local SQLite store")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Unused for local observed inventory")]
        public string StoreConnectionString { get; set; }

        [Option("seen-within-days", Required = false, Default = 30, HelpText = "Count and display identifiers seen within this many days; use 0 for all observations")]
        public int SeenWithinDays { get; set; }

        [Option("limit", Required = false, Default = 10, HelpText = "Number of most recently observed values displayed for each kind; use 0 for counts only")]
        public int Limit { get; set; }

        [Option("history-limit", Required = false, Default = 5, HelpText = "Number of recent fetch-observed executions displayed; use 0 to hide history")]
        public int HistoryLimit { get; set; }
    }

    [Verb("prune-observed", HelpText = "Deletes stale observed inventory and optionally stale per-identifier driver anchors")]
    public class PruneObservedInventoryOptions : IMetadataStoreOptions
    {
        [Option("store-alias", Required = false, HelpText = "Store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Store containing observed inventory")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; pruning currently requires a local SQLite store")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Unused for local observed inventory")]
        public string StoreConnectionString { get; set; }

        [Option("older-than-days", Required = false, Default = 180, HelpText = "Delete observations not seen for this many days")]
        public int OlderThanDays { get; set; }

        [Option("prune-driver-anchors", Required = false, Default = false, HelpText = "Also delete stale per-identifier driver anchors that have no active observation and are not part of a checkpoint")]
        public bool PruneDriverAnchors { get; set; }

        [Option("dry-run", Required = false, Default = false, HelpText = "Show how many observations would be deleted without modifying SQLite")]
        public bool DryRun { get; set; }
    }

    [Verb("query", HelpText = "Query package metadata from a package store")]
    public class QueryMetadataOptions : IMetadataStoreOptions, IMetadataFilterOptions
    {
        [Option("store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Store to query")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }

        [Option("package-type", Required = true, HelpText = "Type of package to query")]
        public string PackageType { get; set; }

        [Option("id-filter", Required = false, Separator = '+', HelpText = "ID filter")]
        public IEnumerable<string> IdFilter { get; set; }

        [Option("file-hash", Required = false, HelpText = "File hash", SetName = "files")]
        public string FileHash { get; set; }

        [Option("title-filter", Required = false, HelpText = "Title filter")]
        public string TitleFilter { get; set; }

        [Option("hwid-filter", Required = false, HelpText = "Hardware ID filter")]
        public string HardwareIdFilter { get; set; }

        [Option("computer-hwid-filter", Required = false, HelpText = "Computer hardware ID filter")]
        public string ComputerHardwareIdFilter { get; set; }

        [Option("product-filter", Required = false, Separator = '+', HelpText = "Product filter")]
        public IEnumerable<string> ProductsFilter { get; set; }

        [Option("classification-filter", Required = false, Separator = '+', HelpText = "Classification filter")]
        public IEnumerable<string> ClassificationsFilter { get; set; }

        [Option("kbarticle-filter", Required = false, Separator = '+', HelpText = "KB article filter (numbers only)")]
        public IEnumerable<string> KbArticleFilter { get; set; }

        [Option("skip-superseded", Required = false, Default = false, HelpText = "Ignore superseded updates")]
        public bool SkipSuperseded { get; set; }

        [Option("count-only", Required = false, Default = false, HelpText = "Count updates, do not display update information")]
        public bool CountOnly { get; set; }

        [Option("first", Required = false, Default = 0, HelpText = "Display first x updates only")]
        public int FirstX { get; set; }

        [Option("json-out-path", Required = false, HelpText = "Save results as JSON to the specified path")]
        public string JsonOutPath { get; set; }

        [Option("include-extended-metadata", Required = false, Default = false, HelpText = "Include extended metadata when saving to JSON query result.")]
        public bool IncludeExtendedMetadata { get; set; }
    }

    [Verb("match-driver", HelpText = "Find drivers")]
    public class MatchDriverOptions : IMetadataStoreOptions
    {
        [Option("store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Store to match drivers from")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }

        [Option("hwid", Required = true, Separator = '+', HelpText = "Match drivers for this list of hardware ids; Add HwIds from specific to generic")]
        public IEnumerable<string> HardwareIds { get; set; }

        [Option("computer-hwid", Required = false, Separator = '+', HelpText = "Match drivers that target these computer hardware ids.")]
        public IEnumerable<string> ComputerHardwareIds { get; set; }

        [Option("installed-prerequisites", Required = true, Separator = '+', HelpText = "Prerequisites installed on the target computer. Used for driver applicability checks")]
        public IEnumerable<string> InstalledPrerequisites { get; set; }
    }

    [Verb("export", HelpText = "Export select update metadata from a metadata source.")]
    public class MetadataSourceExportOptions : IMetadataStoreOptions, IMetadataFilterOptions
    {
        [Option("store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("store-path", Required = false, HelpText = "Store to export from")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }

        [Option("export-file", Required = true, HelpText = "File where to export updates. If the file exists, it will be overwritten.")]
        public string ExportFile { get; set; }

        [Option("server-config", Required = true, HelpText = "JSON file containing server configuration./")]
        public string ServerConfigFile { get; set; }

        [Option("product-filter", Required = false, Separator = '+', HelpText = "Product filter")]
        public IEnumerable<string> ProductsFilter { get; set; }

        [Option("classification-filter", Required = false, Separator = '+', HelpText = "Classification filter")]
        public IEnumerable<string> ClassificationsFilter { get; set; }

        [Option("id-filter", Required = false, Separator = '+', HelpText = "ID filter")]
        public IEnumerable<string> IdFilter { get; set; }

        [Option("title-filter", Required = false, HelpText = "Title filter")]
        public string TitleFilter { get; set; }

        [Option("hwid-filter", Required = false, HelpText = "Hardware ID filter")]
        public string HardwareIdFilter { get; set; }

        [Option("computer-hwid-filter", Required = false, HelpText = "Computer hardware ID filter")]
        public string ComputerHardwareIdFilter { get; set; }

        [Option("kbarticle-filter", Required = false, Separator = '+', HelpText = "KB article filter (numbers only)")]
        public IEnumerable<string> KbArticleFilter { get; set; }

        [Option("skip-superseded", Required = false, Default = false, HelpText = "Do not export superseded updates")]
        public bool SkipSuperseded { get; set; }

        [Option("first", Required = false, Default = 0, HelpText = "Export only the first x updates")]
        public int FirstX { get; set; }
    }

    [Verb("run-upstream-server", HelpText = "Serve updates to downstream servers")]
    public class RunUpstreamServerOptions : IMetadataStoreOptions
    {
        [Option("metadata-store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("metadata-store-path", Required = false, HelpText = "Package metadata store to server packages from")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }

        [Option("content-source", Required = false, HelpText = "Path to content source")]
        public string ContentSourcePath { get; set; }

        [Option("service-config", Required = false, HelpText = "Path to service configuration JSON file")]
        public string ServiceConfigurationPath { get; set; }

        [Option("port", Required = false, Default = 32150, HelpText = "The port to bind the server to.")]
        public int Port { get; set; }

        [Option("endpoint", Required = false, Default = "*", HelpText = "The port to bind the server to.")]
        public string Endpoint { get; set; }
    }

    [Verb("run-update-server", HelpText = "Serve updates to Windows Update clients")]
    public class RunUpdateServerOptions : IMetadataStoreOptions
    {
        [Option("metadata-store-alias", Required = false, HelpText = "Destination store alias")]
        public string Alias { get; set; }

        [Option("metadata-store-path", Required = false, HelpText = "Package metadata store to server packages from")]
        public string Path { get; set; }

        [Option("store-type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }

        [Option("content-source", Required = false, HelpText = "Path to content source")]
        public string ContentSourcePath { get; set; }

        [Option("service-config", Required = false, HelpText = "Path to service configuration JSON file")]
        public string ServiceConfigurationPath { get; set; }

        [Option("port", Required = false, Default = 32150, HelpText = "The port to bind the server to.")]
        public int Port { get; set; }

        [Option("endpoint", Required = false, Default = "*", HelpText = "The port to bind the server to.")]
        public string Endpoint { get; set; }

    }

    [Verb("copy-metadata", HelpText = "Copy packages from one repository to another")]
    public class MetadataCopyOptions : IMetadataFilterOptions
    {
        [Option("source-alias", Required = false, HelpText = "Destination store alias")]
        public string SourceAlias { get; set; }

        [Option("source-path", Required = false, HelpText = "Package metadata source")]
        public string SourcePath { get; set; }

        [Option("source-type", Required = false, Default = "local", HelpText = "Source store type; local (default), azure-blob, azure-table etc.")]
        public string SourceType { get; set; }

        [Option("source-connection-string", Required = false, HelpText = "Source connection string; required for non-local sources")]
        public string SourceConnectionString { get; set; }

        [Option("destination-alias", Required = false, HelpText = "Destination store alias")]
        public string DestinationAlias { get; set; }

        [Option("destination-path", Required = false, HelpText = "Package metadata destination")]
        public string DestionationPath { get; set; }

        [Option("destination-type", Required = false, Default = "local", HelpText = "Destination store type; local (default), azure-blob, azure-table etc.")]
        public string DestinationType { get; set; }

        [Option("destination-connection-string", Required = false, HelpText = "Destination connection string; required for non-local destinations")]
        public string DestinationConnectionString { get; set; }

        [Option("id-filter", Required = false, Separator = '+', HelpText = "ID filter")]
        public IEnumerable<string> IdFilter { get; set; }

        [Option("title-filter", Required = false, HelpText = "Title filter")]
        public string TitleFilter { get; set; }

        [Option("hwid-filter", Required = false, HelpText = "Hardware ID filter")]
        public string HardwareIdFilter { get; set; }

        [Option("computer-hwid-filter", Required = false, HelpText = "Computer hardware ID filter")]
        public string ComputerHardwareIdFilter { get; set; }

        [Option("product-filter", Required = false, Separator = '+', HelpText = "Product filter")]
        public IEnumerable<string> ProductsFilter { get; set; }

        [Option("kbarticle-filter", Required = false, Separator = '+', HelpText = "KB article filter (numbers only)")]
        public IEnumerable<string> KbArticleFilter { get; set; }

        [Option("classification-filter", Required = false, Separator = '+', HelpText = "Classification filter")]
        public IEnumerable<string> ClassificationsFilter { get; set; }

        [Option("skip-superseded", Required = false, Default = false, HelpText = "Do not serve superseded updates")]
        public bool SkipSuperseded { get; set; }

        [Option("first", Required = false, Default = 0, HelpText = "Copy only the first x updates")]
        public int FirstX { get; set; }
    }

    [Verb("create-store-alias", HelpText = "Saves store information and create an alias for it")]
    public class StoreAliasCreateOptions : IMetadataStoreOptions
    {
        [Option("alias", Required = true, HelpText = "Alias for this store configuration")]
        public string Alias { get; set; }

        [Option("path", Required = true, HelpText = "Store path. Local path for a local path; store name for a cloud store")]
        public string Path { get; set; }

        [Option("type", Required = false, Default = "local", HelpText = "Store type; local (default) or azure")]
        public string Type { get; set; }

        [Option("connection-string", Required = false, HelpText = "Azure connection string; required when the store type is azure")]
        public string StoreConnectionString { get; set; }
    }

    [Verb("delete-store-alias", HelpText = "Deletes a store configuration by alias")]
    public class StoreAliasDeleteOptions
    {
        [Option("alias", Required = true, HelpText = "Delete only the specified alias", SetName ="specific")]
        public string Alias { get; set; }

        [Option("all", Required = true, HelpText = "Delete all aliases", SetName = "all")]
        public bool All{ get; set; }
    }

    [Verb("list-store-aliases", HelpText = "Lists stored store aliases")]
    public class StoreAliasListOptions
    {
        [Option("alias", Required = false, HelpText = "List only the specified alias")]
        public string Alias { get; set; }
    }
}
