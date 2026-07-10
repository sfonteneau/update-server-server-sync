// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using System.Threading;
using Microsoft.PackageGraph.MicrosoftUpdate.Source;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    /// <summary>
    /// Implements operations to fetch update metadata from an upstream update server
    /// </summary>
    class MetadataSync
    {
        public static void FetchConfiguration(FetchConfigurationOptions options)
        {
            MicrosoftUpdate.Source.Endpoint upstreamEndpoint;
            if (!string.IsNullOrEmpty(options.UpstreamEndpoint))
            {
                upstreamEndpoint = new MicrosoftUpdate.Source.Endpoint(options.UpstreamEndpoint);
            }
            else
            {
                upstreamEndpoint = MicrosoftUpdate.Source.Endpoint.Default;
            }

            var server = new UpstreamServerClient(upstreamEndpoint);
            server.MetadataQueryProgress += Server_MetadataQueryProgress;
            var configData = server.GetServerConfigData().GetAwaiter().GetResult();

            File.WriteAllText(options.OutFile, JsonConvert.SerializeObject(configData));
        }

        public static void ReIndex(ReindexStoreOptions options)
        {
            var sourceToUpdate = MetadataStoreCreator.OpenFromOptions(options as IMetadataStoreOptions);
            if (sourceToUpdate == null)
            {
                return;
            }

            using(sourceToUpdate)
            {
                if (sourceToUpdate.IsMetadataIndexingSupported)
                {
                    sourceToUpdate.PackageIndexingProgress += Program.OnPackageIndexingProgress;
                    if (sourceToUpdate.IsReindexingRequired || options.ForceReindex)
                    {
                        Console.WriteLine("ReIndexing ...");
                        sourceToUpdate.ReIndex();
                        ConsoleOutput.WriteGreen("Done!");
                    }
                    else
                    {
                        ConsoleOutput.WriteGreen("Indexing not required!");
                    }
                }
                else
                {
                    ConsoleOutput.WriteRed("Package store does not support indexing!");
                }
            }
        }

        public static void FetchCategories(FetchCategoriesOptions options)
        {
            MicrosoftUpdate.Source.Endpoint upstreamEndpoint;
            if (!string.IsNullOrEmpty(options.UpstreamEndpoint))
            {
                upstreamEndpoint = new MicrosoftUpdate.Source.Endpoint(options.UpstreamEndpoint);
            }
            else
            {
                upstreamEndpoint = MicrosoftUpdate.Source.Endpoint.Default;
            }

            if (!string.IsNullOrEmpty(options.AccountName) &&
                !string.IsNullOrEmpty(options.AccountGuid))
            {
                throw new NotImplementedException();
            }

            var destinationStore = MetadataStoreCreator.CreateFromOptions(options as IMetadataStoreOptions);
            if (destinationStore == null)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"Getting list of categories. This might take up to 1 minute ...");
            using (destinationStore)
            {
                var microsoftUpdateCategoriesSource = new UpstreamCategoriesSource(upstreamEndpoint);
                microsoftUpdateCategoriesSource.MetadataCopyProgress += Program.OnPackageCopyProgress;
                var cancellationToken = new CancellationTokenSource();
                microsoftUpdateCategoriesSource.CopyTo(destinationStore, cancellationToken.Token);
            }

            Console.WriteLine();
            ConsoleOutput.WriteGreen("Done!");
        }

        public static void FetchPackagesUpdates(FetchPackagesOptions options)
        {
            var store = MetadataStoreCreator.CreateFromOptions(options as IMetadataStoreOptions);
            if (store == null)
            {
                return;
            }

            switch (options.EndpointType)
            {
                case FetchPackagesOptions.MicrosoftUpdateEndpoint:
                    FetchMicrosoftUpdatePackages(options, store);
                    break;

                default:
                    throw new NotImplementedException();
            }
        }

        private static void FetchMicrosoftUpdatePackages(FetchPackagesOptions options, IMetadataStore store)
        {
            var upstreamEndpoint = string.IsNullOrEmpty(options.UpstreamEndpoint) ? MicrosoftUpdate.Source.Endpoint.Default : new MicrosoftUpdate.Source.Endpoint(options.UpstreamEndpoint);

            if (!string.IsNullOrEmpty(options.AccountName) &&
                !string.IsNullOrEmpty(options.AccountGuid))
            {
                throw new NotImplementedException();
            }

            using (store)
            {
                var cancellationToken = new CancellationTokenSource();
                if (options.RefreshCategories || StoreIsEmpty(store))
                {
                    var microsoftUpdateCategoriesSource = new UpstreamCategoriesSource(upstreamEndpoint);

                    Console.WriteLine($"Getting list of categories. This might take up to 1 minute ...");

                    microsoftUpdateCategoriesSource.MetadataCopyProgress += Program.OnPackageCopyProgress;
                    microsoftUpdateCategoriesSource.CopyTo(store, cancellationToken.Token);
                }
                else
                {
                    Console.WriteLine("Using cached categories. Pass --refresh-categories to update them.");
                }

                if (HasValues(options.Ids))
                {
                    var server = new UpstreamServerClient(upstreamEndpoint);

                    foreach (var updateId in options.Ids)
                    {
                        if (Guid.TryParse(updateId, out var updateIdGuid))
                        {
                            Console.WriteLine();
                            Console.Write($"Searching for package {updateId}");
                            var foundPackage = server.TryGetExpiredUpdate(updateIdGuid, 300, 100).GetAwaiter().GetResult();
                            if (foundPackage == null)
                            {
                                ConsoleOutput.WriteRed($" Not found!");
                            }
                            else
                            {
                                ConsoleOutput.WriteGreen($" Found!");
                                store.AddPackage(foundPackage);
                            }
                        }
                        else
                        {
                            ConsoleOutput.WriteRed($"Update id must be in GUID format: {updateId}");
                            return;
                        }
                    }
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("Creating the query ...");
                    UpstreamSourceFilter sourceFilter;
                    try
                    {
                        sourceFilter = MetadataSync.CreateValidFilterFromOptions(options, store);
                    }
                    catch (Exception ex)
                    {
                        ConsoleOutput.WriteRed(ex.Message);
                        return;
                    }

                    MetadataQuery.PrintFilter(sourceFilter, store);

                    Console.WriteLine($"Getting list of updates ...");
                    string anchorKey = BuildSyncAnchorKey(upstreamEndpoint, sourceFilter);
                    string oldAnchor = null;

                    if (!options.IgnoreSyncAnchor && store is ISyncAnchorStore anchorStore && anchorStore.TryGetSyncAnchor(anchorKey, out var storedAnchor))
                    {
                        oldAnchor = storedAnchor;
                        Console.WriteLine("Using saved upstream sync anchor. Only changed revisions will be requested.");
                    }
                    else if (options.IgnoreSyncAnchor)
                    {
                        Console.WriteLine("Ignoring saved upstream sync anchor. A full revision list will be requested.");
                    }

                    var microsoftUpdateSource = new UpstreamUpdatesSource(upstreamEndpoint, sourceFilter, oldAnchor)
                    {
                        BatchSize = Math.Max(1, options.MetadataBatchSize)
                    };
                    microsoftUpdateSource.MetadataCopyProgress += Program.OnPackageCopyProgress;

                    try
                    {
                        microsoftUpdateSource.CopyTo(store, cancellationToken.Token);
                    }
                    catch (Exception ex) when (!string.IsNullOrEmpty(oldAnchor))
                    {
                        ConsoleOutput.WriteRed($"Incremental fetch failed with the saved anchor: {ex.Message}");
                        Console.WriteLine("Clearing the saved anchor and retrying once with a full revision list.");

                        if (store is ISyncAnchorStore retryAnchorStore)
                        {
                            retryAnchorStore.ClearSyncAnchor(anchorKey);
                        }

                        microsoftUpdateSource = new UpstreamUpdatesSource(upstreamEndpoint, sourceFilter, null)
                        {
                            BatchSize = Math.Max(1, options.MetadataBatchSize)
                        };
                        microsoftUpdateSource.MetadataCopyProgress += Program.OnPackageCopyProgress;
                        microsoftUpdateSource.CopyTo(store, cancellationToken.Token);
                    }

                    if (store is ISyncAnchorStore finalAnchorStore && !string.IsNullOrEmpty(microsoftUpdateSource.NewAnchor))
                    {
                        finalAnchorStore.SetSyncAnchor(anchorKey, microsoftUpdateSource.NewAnchor);
                        Console.WriteLine("Saved upstream sync anchor for the next incremental fetch.");
                    }

                    Console.WriteLine();
                    Console.WriteLine("Done!");
                }
            }
        }

        private static void UpdateConsoleForMessageRefresh()
        {
            if (!Console.IsOutputRedirected)
            {
                Console.CursorLeft = 0;
            }
            else
            {
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Handles progress notifications from a metadata query on an upstream server.
        /// Prints progress information to the console
        /// </summary>
        /// <param name="sender">The upstream server client that raised the event</param>
        /// <param name="e">Progress information</param>
        private static void Server_MetadataQueryProgress(object sender, MetadataQueryProgress e)
        {
            switch (e.CurrentTask)
            {
                case MetadataQueryStage.AuthenticateStart:
                    Console.Write("Acquiring new access token...");
                    break;

                case MetadataQueryStage.GetServerConfigStart:
                    Console.Write("Retrieving service configuration data...");
                    break;

                case MetadataQueryStage.AuthenticateEnd:
                case MetadataQueryStage.GetServerConfigEnd:
                case MetadataQueryStage.GetRevisionIdsEnd:
                    ConsoleOutput.WriteGreen("Done!");
                    break;

                case MetadataQueryStage.GetRevisionIdsStart:
                    Console.Write("Retrieving revision IDs...");
                    break;

                case MetadataQueryStage.GetUpdateMetadataStart:
                    Console.Write("Retrieving updates metadata [{0}]: 0%", e.Maximum);
                    break;

                case MetadataQueryStage.GetUpdateMetadataEnd:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Retrieving updates metadata [{0}]: 100.00%", e.Maximum);
                    ConsoleOutput.WriteGreen(" Done!");
                    break;

                case MetadataQueryStage.GetUpdateMetadataProgress:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Retrieving updates metadata [{0}]: {1:000.00}%", e.Maximum, e.PercentDone);
                    break;
            }
        }

        private static bool HasValues(IEnumerable<string> values)
        {
            return values != null && values.Any(value => !string.IsNullOrWhiteSpace(value));
        }

        private static bool StoreIsEmpty(IMetadataStore metadataSource)
        {
            return metadataSource.GetPackageIdentities().Count == 0;
        }

        private static List<Guid> CreateFilterListForCategory<T>(IEnumerable<string> userFilterList, IMetadataStore metadataSource, bool includeAllWhenEmpty)
        {
            var userFilters = userFilterList?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList() ?? new List<string>();

            List<Guid> filterList;
            if (userFilters.Count > 0)
            {
                filterList = new List<Guid>();
                foreach (var guidString in userFilters)
                {
                    if (!Guid.TryParse(guidString, out Guid guid))
                    {
                        throw new Exception($"Invalid GUID in filter: {guidString}");
                    }

                    filterList.Add(guid);
                }
            }
            else if (includeAllWhenEmpty)
            {
                filterList = metadataSource.OfType<T>()
                    .Select(update => (update as MicrosoftUpdatePackage).Id.ID)
                    .ToList();

                if (filterList.Count == 0)
                {
                    throw new Exception("No category information available to create a filter. Run pre-fetch first or use --refresh-categories.");
                }
            }
            else
            {
                filterList = new List<Guid>();
            }

            return filterList.Distinct().ToList();
        }

        private static List<int> CreateLanguageFilterFromOptions(FetchPackagesOptions options)
        {
            var languageOptions = options.LanguageFilter?.ToList() ?? new List<string>();
            if (languageOptions.Count == 0)
            {
                // Default to English only. This avoids syncing every localized metadata branch.
                return new List<int> { 1033 };
            }

            if (languageOptions.Any(language =>
                string.Equals(language, "all", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language, "*", StringComparison.OrdinalIgnoreCase)))
            {
                return new List<int>();
            }

            var languages = new List<int>();
            foreach (var language in languageOptions)
            {
                if (int.TryParse(language, out var lcid))
                {
                    languages.Add(lcid);
                    continue;
                }

                var normalizedLanguage = language.Trim().ToLowerInvariant();
                switch (normalizedLanguage)
                {
                    case "en":
                    case "en-us":
                    case "english":
                        languages.Add(1033);
                        break;
                    case "fr":
                    case "fr-fr":
                    case "french":
                        languages.Add(1036);
                        break;
                    case "de":
                    case "de-de":
                    case "german":
                        languages.Add(1031);
                        break;
                    case "es":
                    case "es-es":
                    case "spanish":
                        languages.Add(3082);
                        break;
                    case "it":
                    case "it-it":
                    case "italian":
                        languages.Add(1040);
                        break;
                    default:
                        throw new Exception($"Unsupported language filter '{language}'. Use an LCID such as 1033, a known short name such as en, or all.");
                }
            }

            return languages.Distinct().ToList();
        }

        private static string BuildSyncAnchorKey(MicrosoftUpdate.Source.Endpoint endpoint, UpstreamSourceFilter filter)
        {
            var keyData = new
            {
                Type = "microsoft-update-revision-list",
                Endpoint = endpoint.URI,
                Products = filter.ProductsFilter.OrderBy(value => value).Select(value => value.ToString("D")).ToList(),
                Classifications = filter.ClassificationsFilter.OrderBy(value => value).Select(value => value.ToString("D")).ToList(),
                Languages = filter.LanguagesFilter.OrderBy(value => value).ToList()
            };

            var json = JsonConvert.SerializeObject(keyData);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
            return "sync-anchor:mu:" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static UpstreamSourceFilter CreateValidFilterFromOptions(FetchPackagesOptions options, IMetadataStore metadataSource)
        {
            var hasProductFilter = HasValues(options.ProductsFilter);
            if (!hasProductFilter && !options.AllowAllProducts)
            {
                throw new Exception(
                    "No --product-filter was provided. A classification-only fetch asks Microsoft Update for that classification across every product and can take hours or create a huge store. " +
                    "Add --product-filter <PRODUCT_GUID>, or add --allow-all-products if you really want the full catalog.");
            }

            if (!hasProductFilter && options.AllowAllProducts)
            {
                ConsoleOutput.WriteRed("Warning: --allow-all-products selected. This can be extremely slow and large.");
            }

            List<Guid> productFilter = CreateFilterListForCategory<ProductCategory>(
                options.ProductsFilter,
                metadataSource,
                includeAllWhenEmpty: true);

            List<Guid> classificationFilter = CreateFilterListForCategory<ClassificationCategory>(
                options.ClassificationsFilter,
                metadataSource,
                includeAllWhenEmpty: true);

            List<int> languageFilter = CreateLanguageFilterFromOptions(options);
            bool stripUnrequestedLocalizedProperties = languageFilter.Count > 0 && !options.KeepAllLocalizedProperties;

            return new UpstreamSourceFilter(productFilter, classificationFilter, languageFilter, stripUnrequestedLocalizedProperties);
        }
    }
}
