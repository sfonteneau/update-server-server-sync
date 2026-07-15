// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ClientSync
{
    /// <summary>
    /// Update server implementation. The client-facing service reads a precomputed
    /// SQLite graph and driver index, with a bounded metadata cache for selected packages.
    /// </summary>
    public partial class ClientSyncWebService : IClientSyncWebService
    {
        /// <summary>
        /// Direct SQLite read model used for all client requests.
        /// </summary>
        public IClientSyncMetadataStore MetadataSource { get; private set; }

        private Config ServiceConfiguration;
        private const int MaxUpdatesInResponse = 50;
        private static readonly SemaphoreSlim ScanConcurrencyGate = new(1, 1);
        private static long ScanSequence;
        private string ContentRoot;

        /// <summary>
        /// Default constructor.
        /// </summary>
        public ClientSyncWebService()
        {
            ApprovedSoftwareUpdates = new HashSet<MicrosoftUpdatePackageIdentity>();
            ApprovedDriverUpdates = new HashSet<MicrosoftUpdatePackageIdentity>();
            DeniedDriverUpdates = new HashSet<MicrosoftUpdatePackageIdentity>();
        }

        /// <summary>
        /// Sets the host name for the server that serves update content. Microsoft
        /// Update URLs remain preferred when they are present in the metadata.
        /// </summary>
        public void SetContentURLBase(string hostName)
        {
            ContentRoot = hostName;
        }

        private string GetPreferredDownloadUrl(UpdateFile file, IContentFileDigest digest)
        {
            var urlsForDigest = file.Urls?
                .Where(url => string.Equals(
                    url.DigestBase64,
                    digest.DigestBase64,
                    StringComparison.Ordinal))
                .ToList();

            var microsoftUrl = urlsForDigest?
                .FirstOrDefault(url => !string.IsNullOrEmpty(url.MuUrl))?
                .MuUrl
                ?? file.Urls?
                    .FirstOrDefault(url => !string.IsNullOrEmpty(url.MuUrl))?
                    .MuUrl;

            if (!string.IsNullOrEmpty(microsoftUrl))
            {
                return microsoftUrl;
            }

            if (!string.IsNullOrEmpty(ContentRoot))
            {
                return $"{ContentRoot}/{digest.HexString.ToLowerInvariant()}";
            }

            return urlsForDigest?
                .FirstOrDefault(url => !string.IsNullOrEmpty(url.UssUrl))?
                .UssUrl
                ?? file.Urls?
                    .FirstOrDefault(url => !string.IsNullOrEmpty(url.UssUrl))?
                    .UssUrl;
        }

        private FileLocation GetFileLocation(UpdateFile file, byte[] requestedDigest = null)
        {
            var requestedDigestBase64 = requestedDigest == null
                ? null
                : Convert.ToBase64String(requestedDigest);

            var digest = !string.IsNullOrEmpty(requestedDigestBase64)
                ? file.Digests?.FirstOrDefault(candidate => string.Equals(
                    candidate.DigestBase64,
                    requestedDigestBase64,
                    StringComparison.Ordinal))
                : file.Digests?
                    .FirstOrDefault(candidate => file.Urls?.Any(url =>
                        string.Equals(
                            url.DigestBase64,
                            candidate.DigestBase64,
                            StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(url.MuUrl)) == true)
                    ?? file.Digest;

            if (digest == null)
            {
                return null;
            }

            var url = GetPreferredDownloadUrl(file, digest);
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            return new FileLocation
            {
                FileDigest = Convert.FromBase64String(digest.DigestBase64),
                Url = url
            };
        }

        /// <summary>
        /// Sets the service configuration.
        /// </summary>
        public void SetServiceConfiguration(Config serviceConfiguration)
        {
            ServiceConfiguration = serviceConfiguration;
            UpdateServiceConfigurationLastChange();
        }

        /// <summary>
        /// Sets the direct SQLite client-sync store.
        /// </summary>
        public void SetPackageStore(IClientSyncMetadataStore metadataSource)
        {
            MetadataSource = metadataSource ?? throw new ArgumentNullException(nameof(metadataSource));
            UpdateServiceConfigurationLastChange();
        }

        private void UpdateServiceConfigurationLastChange()
        {
            if (ServiceConfiguration == null || MetadataSource == null)
            {
                return;
            }

            var generation = MetadataSource.GetPublishedCatalogInfo();
            if (generation.Generation > 0
                && generation.LastChanged != DateTimeOffset.MinValue)
            {
                ServiceConfiguration.LastChange = generation.LastChanged.UtcDateTime;
            }
        }

        private void EnsureMetadataSourceAvailable()
        {
            if (MetadataSource == null)
            {
                throw new FaultException("Metadata source is not configured.");
            }
        }

        /// <inheritdoc />
        public Task<Config> GetConfig2Async(ClientConfiguration clientConfiguration)
        {
            EnsureMetadataSourceAvailable();
            UpdateServiceConfigurationLastChange();
            return Task.FromResult(ServiceConfiguration);
        }

        /// <inheritdoc />
        public Task<Config> GetConfigAsync(string protocolVersion)
        {
            EnsureMetadataSourceAvailable();
            UpdateServiceConfigurationLastChange();
            return Task.FromResult(ServiceConfiguration);
        }

        /// <inheritdoc />
        public Task<Cookie> GetCookieAsync(
            AuthorizationCookie[] authCookies,
            Cookie oldCookie,
            DateTime lastChange,
            DateTime currentTime,
            string protocolVersion)
        {
            EnsureMetadataSourceAvailable();
            return Task.FromResult(new Cookie
            {
                Expiration = DateTime.Now.AddDays(5),
                EncryptedData = new byte[12]
            });
        }

        /// <inheritdoc />
        public Task<ExtendedUpdateInfo2> GetExtendedUpdateInfo2Async(
            Cookie cookie,
            UpdateIdentity[] updateIDs,
            XmlUpdateFragmentType[] infoTypes,
            string[] locales,
            string deviceAttributes)
        {
            EnsureMetadataSourceAvailable();

            var requestedUpdates = new List<ClientSyncPackageRecord>();
            foreach (var updateID in updateIDs ?? Array.Empty<UpdateIdentity>())
            {
                var identity = new MicrosoftUpdatePackageIdentity(
                    updateID.UpdateID,
                    updateID.RevisionNumber);
                if (!MetadataSource.TryGetPackage(identity, out var package))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        $"Ignoring a stale client update identity that is not present in the published catalog: {identity}");
                    continue;
                }

                requestedUpdates.Add(package);
            }

            var requestedInfoTypes = infoTypes ?? Array.Empty<XmlUpdateFragmentType>();
            var updateDataList = new List<UpdateData>();

            if (requestedInfoTypes.Contains(XmlUpdateFragmentType.Extended))
            {
                foreach (var requestedUpdate in requestedUpdates)
                {
                    updateDataList.Add(new UpdateData
                    {
                        ID = requestedUpdate.RevisionId,
                        Xml = GetExtendedFragment(requestedUpdate.Package.Id)
                    });
                }
            }

            if (requestedInfoTypes.Contains(XmlUpdateFragmentType.LocalizedProperties))
            {
                foreach (var requestedUpdate in requestedUpdates)
                {
                    var localizedXml = GetLocalizedProperties(
                        requestedUpdate.Package.Id,
                        locales);
                    if (!string.IsNullOrEmpty(localizedXml))
                    {
                        updateDataList.Add(new UpdateData
                        {
                            ID = requestedUpdate.RevisionId,
                            Xml = localizedXml
                        });
                    }
                }
            }

            var files = requestedUpdates
                .SelectMany(update => MetadataSource.GetFiles(update.Package.Id))
                .GroupBy(file => file.Digest?.DigestBase64 ?? string.Empty, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var fileList = files
                .Select(file => GetFileLocation(file))
                .Where(location => location != null)
                .ToList();

            return Task.FromResult(new ExtendedUpdateInfo2
            {
                Updates = updateDataList.Count == 0 ? null : updateDataList.ToArray(),
                FileLocations = fileList.Count == 0 ? null : fileList.ToArray()
            });
        }

        private string GetCoreFragment(MicrosoftUpdatePackageIdentity updateIdentity)
        {
            using var xmlStream = MetadataSource.GetMetadata(updateIdentity);
            using var xmlReader = new StreamReader(xmlStream, Encoding.Unicode);
            return UpdateXmlTransformer.GetCoreFragmentFromMetadataXml(xmlReader.ReadToEnd());
        }

        private string GetExtendedFragment(MicrosoftUpdatePackageIdentity updateIdentity)
        {
            using var xmlStream = MetadataSource.GetMetadata(updateIdentity);
            using var xmlReader = new StreamReader(xmlStream, Encoding.Unicode);
            return UpdateXmlTransformer.GetExtendedFragmentFromMetadataXml(xmlReader.ReadToEnd());
        }

        private string GetLocalizedProperties(
            MicrosoftUpdatePackageIdentity updateIdentity,
            string[] languages)
        {
            using var xmlStream = MetadataSource.GetMetadata(updateIdentity);
            using var xmlReader = new StreamReader(xmlStream, Encoding.Unicode);
            return UpdateXmlTransformer.GetLocalizedPropertiesFromMetadataXml(
                xmlReader.ReadToEnd(),
                languages);
        }

        /// <inheritdoc />
        public Task<ExtendedUpdateInfo> GetExtendedUpdateInfoAsync(
            Cookie cookie,
            int[] revisionIDs,
            XmlUpdateFragmentType[] infoTypes,
            string[] locales,
            string deviceAttributes)
        {
            EnsureMetadataSourceAvailable();

            var requestedUpdates = new List<ClientSyncPackageRecord>();
            foreach (var requestedRevision in revisionIDs ?? Array.Empty<int>())
            {
                if (!MetadataSource.TryGetPackage(requestedRevision, out var package))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        $"Ignoring stale client revision ID {requestedRevision}; it is not present in the published catalog.");
                    continue;
                }

                requestedUpdates.Add(package);
            }

            var requestedInfoTypes = infoTypes ?? Array.Empty<XmlUpdateFragmentType>();
            var updateDataList = new List<UpdateData>();

            if (requestedInfoTypes.Contains(XmlUpdateFragmentType.Extended))
            {
                foreach (var requestedUpdate in requestedUpdates)
                {
                    updateDataList.Add(new UpdateData
                    {
                        ID = requestedUpdate.RevisionId,
                        Xml = GetExtendedFragment(requestedUpdate.Package.Id)
                    });
                }
            }

            if (requestedInfoTypes.Contains(XmlUpdateFragmentType.LocalizedProperties))
            {
                foreach (var requestedUpdate in requestedUpdates)
                {
                    var localizedXml = GetLocalizedProperties(
                        requestedUpdate.Package.Id,
                        locales);
                    if (!string.IsNullOrEmpty(localizedXml))
                    {
                        updateDataList.Add(new UpdateData
                        {
                            ID = requestedUpdate.RevisionId,
                            Xml = localizedXml
                        });
                    }
                }
            }

            var files = requestedUpdates
                .SelectMany(update => MetadataSource.GetFiles(update.Package.Id))
                .GroupBy(file => file.Digest?.DigestBase64 ?? string.Empty, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var fileList = files
                .Select(file => GetFileLocation(file))
                .Where(location => location != null)
                .ToList();

            return Task.FromResult(new ExtendedUpdateInfo
            {
                Updates = updateDataList.Count == 0 ? null : updateDataList.ToArray(),
                FileLocations = fileList.Count == 0 ? null : fileList.ToArray()
            });
        }

        /// <inheritdoc />
        public Task<GetFileLocationsResults> GetFileLocationsAsync(
            Cookie cookie,
            byte[][] fileDigests)
        {
            EnsureMetadataSourceAvailable();
            var locations = MetadataSource
                .GetFileLocations(fileDigests ?? Array.Empty<byte[]>())
                .Select(location => new FileLocation
                {
                    FileDigest = location.Digest,
                    Url = location.Url
                })
                .ToArray();

            return Task.FromResult(new GetFileLocationsResults
            {
                FileLocations = locations,
                NewCookie = cookie
            });
        }

        public Task<GetTimestampsResponse> GetTimestampsAsync(GetTimestampsRequest request)
        {
            throw new NotImplementedException();
        }

        public Task<RefreshCacheResult[]> RefreshCacheAsync(
            Cookie cookie,
            UpdateIdentity[] globalIDs,
            string deviceAttributes)
        {
            throw new NotImplementedException();
        }

        public Task RegisterComputerAsync(Cookie cookie, ComputerInfo computerInfo)
        {
            throw new NotImplementedException();
        }

        public Task<StartCategoryScanResponse> StartCategoryScanAsync(StartCategoryScanRequest request)
        {
            throw new NotImplementedException();
        }

        public Task<SyncInfo> SyncPrinterCatalogAsync(
            Cookie cookie,
            int[] installedNonLeafUpdateIDs,
            int[] printerUpdateIDs,
            string deviceAttributes)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public async Task<SyncInfo> SyncUpdatesAsync(Cookie cookie, SyncUpdateParameters parameters)
        {
            EnsureMetadataSourceAvailable();
            parameters ??= new SyncUpdateParameters();
            var scanId = Interlocked.Increment(ref ScanSequence);
            var scanType = parameters.SkipSoftwareSync ? "drivers" : "software";
            var queueStopwatch = Stopwatch.StartNew();
            await ScanConcurrencyGate.WaitAsync().ConfigureAwait(false);
            queueStopwatch.Stop();

            var totalStopwatch = Stopwatch.StartNew();
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            Trace.TraceInformation(
                $"Client scan {scanId} started: type={scanType}, queue_ms={queueStopwatch.ElapsedMilliseconds}, " +
                $"devices={parameters.SystemSpec?.Length ?? 0}, installed_non_leaf={parameters.InstalledNonLeafUpdateIDs?.Length ?? 0}, " +
                $"other_cached={parameters.OtherCachedUpdateIDs?.Length ?? 0}, cached_drivers={parameters.CachedDriverIDs?.Length ?? 0}.");

            try
            {
                try
                {
                    RecordObservedInventory(parameters);
                }
                catch (Exception exception)
                {
                    Trace.TraceError(
                        $"Client scan {scanId}: cannot record observed client inventory: {exception}");
                }

                return parameters.SkipSoftwareSync
                    ? await DoDriversSync(parameters).ConfigureAwait(false)
                    : await DoSoftwareUpdateSync(parameters).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    $"Client scan {scanId} failed after {totalStopwatch.ElapsedMilliseconds} ms: {exception}");
                throw;
            }
            finally
            {
                totalStopwatch.Stop();
                var cpuUsed = process.TotalProcessorTime - cpuBefore;
                Trace.TraceInformation(
                    $"Client scan {scanId} completed: type={scanType}, total_ms={totalStopwatch.ElapsedMilliseconds}, " +
                    $"process_cpu_ms={cpuUsed.TotalMilliseconds:F0}.");
                ScanConcurrencyGate.Release();
            }
        }

        private List<MicrosoftUpdatePackageIdentity> GetUpdateIdentitiesFromClientIndexes(
            int[] clientIndexes)
        {
            var requested = (clientIndexes ?? Array.Empty<int>())
                .Distinct()
                .ToArray();
            var identitiesByRevision = MetadataSource.GetPackageIdentities(requested);
            var identities = new List<MicrosoftUpdatePackageIdentity>(requested.Length);
            var missingRevisionIds = new List<int>();
            foreach (var revisionId in requested)
            {
                if (!identitiesByRevision.TryGetValue(revisionId, out var identity))
                {
                    // WUA can retain local revision IDs from a previous database,
                    // server installation or catalog generation. They are local
                    // server IDs, so failing the entire scan would prevent WUA
                    // from learning the current root/detectoid catalog.
                    missingRevisionIds.Add(revisionId);
                    continue;
                }

                identities.Add(identity);
            }

            if (missingRevisionIds.Count > 0)
            {
                var sample = string.Join(", ", missingRevisionIds.Take(20));
                System.Diagnostics.Trace.TraceWarning(
                    $"Ignored {missingRevisionIds.Count} stale client revision ID(s) that are not present " +
                    $"in the published catalog: {sample}" +
                    (missingRevisionIds.Count > 20 ? ", ..." : string.Empty));
            }

            return identities;
        }

        private List<Guid> GetInstalledNotLeafGuidsFromSyncParameters(
            SyncUpdateParameters parameters)
        {
            return GetUpdateIdentitiesFromClientIndexes(
                    parameters.InstalledNonLeafUpdateIDs)
                .Select(update => update.ID)
                .ToList();
        }

        private List<Guid> GetOtherCachedUpdateGuidsFromSyncParameters(
            SyncUpdateParameters parameters)
        {
            return GetUpdateIdentitiesFromClientIndexes(parameters.OtherCachedUpdateIDs)
                .Select(update => update.ID)
                .ToList();
        }
    }
}
