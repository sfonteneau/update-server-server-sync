// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using CommandLine;
using Microsoft.PackageGraph.MicrosoftUpdate;
using Microsoft.PackageGraph.Storage;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    class Program
    {
        private static readonly object ProgressLock = new();

        static void Main(string[] args)
        {
            var verbTypes = new[]
            {
                typeof(MetadataSourceStatusOptions),
                typeof(FetchPackagesOptions),
                typeof(FetchObservedOptions),
                typeof(QueryMetadataOptions),
                typeof(MetadataSourceExportOptions),
                typeof(ContentSyncOptions),
                typeof(RunUpstreamServerOptions),
                typeof(RunUpdateServerOptions),
                typeof(FetchCategoriesOptions),
                typeof(FetchConfigurationOptions),
                typeof(ReindexStoreOptions),
                typeof(CompactStoreOptions),
                typeof(ObservedInventoryStatusOptions),
                typeof(PruneObservedInventoryOptions),
                typeof(MatchDriverOptions),
                typeof(MetadataCopyOptions),
                typeof(StoreAliasListOptions),
                typeof(StoreAliasDeleteOptions),
                typeof(StoreAliasCreateOptions)
            };

            CommandLine.Parser.Default
                .ParseArguments(args, verbTypes)
                .WithParsed<object>(RunCommand)
                .WithNotParsed(failed => Console.WriteLine("Error"));
        }

        private static void RunCommand(object options)
        {
            switch (options)
            {
                case FetchPackagesOptions value:
                    MetadataSync.FetchPackagesUpdates(value);
                    break;
                case FetchObservedOptions value:
                    MetadataSync.FetchObserved(value);
                    break;
                case FetchConfigurationOptions value:
                    MetadataSync.FetchConfiguration(value);
                    break;
                case FetchCategoriesOptions value:
                    MetadataSync.FetchCategories(value);
                    break;
                case ReindexStoreOptions value:
                    MetadataSync.ReIndex(value);
                    break;
                case CompactStoreOptions value:
                    MetadataStoreCreator.CompactStore(value);
                    break;
                case ObservedInventoryStatusOptions value:
                    ObservedInventoryStatusCommand.Show(value);
                    break;
                case PruneObservedInventoryOptions value:
                    ObservedInventoryMaintenanceCommand.Prune(value);
                    break;
                case QueryMetadataOptions value:
                    MetadataQuery.Query(value);
                    break;
                case MatchDriverOptions value:
                    MetadataQuery.MatchDrivers(value);
                    break;
                case MetadataSourceExportOptions value:
                    UpdateMetadataExport.ExportUpdates(value);
                    break;
                case ContentSyncOptions value:
                    ContentSync.SyncContent(value);
                    break;
                case MetadataSourceStatusOptions value:
                    MetadataQuery.Status(value);
                    break;
                case RunUpstreamServerOptions value:
                    UpstreamServer.Run(value);
                    break;
                case RunUpdateServerOptions value:
                    UpdateServer.Run(value);
                    break;
                case MetadataCopyOptions value:
                    MetadataCopy.Run(value);
                    break;
                case StoreAliasListOptions value:
                    MetadataStoreCreator.ListAliases(value);
                    break;
                case StoreAliasDeleteOptions value:
                    MetadataStoreCreator.DeleteAlias(value);
                    break;
                case StoreAliasCreateOptions value:
                    MetadataStoreCreator.CreateAlias(value);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported command options type: {options?.GetType().FullName ?? "null"}",
                        nameof(options));
            }
        }

        private static readonly object ConsoleWriteLock = new();

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

        public static void OnPackageCopyProgress(object sender, PackageStoreEventArgs e)
        {
            lock (ConsoleWriteLock)
            {
                UpdateConsoleForMessageRefresh();

                if (e.Total == 0)
                {
                    Console.Write($"Copying {e.Total} package(s)");
                }
                else
                {
                    Console.Write($"Copying {e.Total} package(s). {e.Current} {Math.Truncate(((double)e.Current * 100) / e.Total)}%");
                }
            }
        }

        public static void OnOpenProgress(object sender, PackageStoreEventArgs e)
        {
            lock (ConsoleWriteLock)
            {
                UpdateConsoleForMessageRefresh();

                if (e.Total == 0)
                {
                    Console.Write(e.Current);
                }
                else
                {
                    Console.Write($"{e.Current}, {Math.Truncate(((double)e.Current * 100) / e.Total)}%");
                }
            }
        }

        public static void OnPackageIndexingProgress(object sender, PackageStoreEventArgs e)
        {
            lock(ProgressLock)
            {
                UpdateConsoleForMessageRefresh();

                if (e.Total == 0)
                {
                    Console.Write($"Indexing {e.Total} package(s)");
                }
                else
                {
                    Console.Write($"Indexing {e.Total} package(s). {e.Current} {Math.Truncate(((double)e.Current * 100) / e.Total)}%");
                }
            }
        }
    }
}
