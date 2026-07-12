// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ClientSync
{
    /// <summary>
    /// Update server implementation. The client-facing service reads the published
    /// catalog directly from SQLite for every request and does not retain a package
    /// catalog, prerequisite graph, driver index or metadata object cache in memory.
    /// </summary>
    public partial class ClientSyncWebService : IClientSyncWebService
    {
        /// <summary>
        /// Direct SQLite read model used for all client requests.
        /// </summary>
        public IClientSyncMetadataStore MetadataSource { get; private set; }

        private Config ServiceConfiguration;
        private const int MaxUpdatesInResponse = 50;
        private string ContentRoot;

        /// <summary>
        /// Default constructor.
        /// </summary>
        public ClientSyncWebService()
        {
            ApprovedSoftwareUpdates = new HashSet<MicrosoftUpdatePackageIdentity>();
            ApprovedDriverUpdates = new HashSet<MicrosoftUpdatePackageIdentity>();
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
                    throw new FaultException($"Update identity not found: {identity}");
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
                    throw new FaultException($"Revision ID not found: {requestedRevision}");
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
        public Task<SyncInfo> SyncUpdatesAsync(Cookie cookie, SyncUpdateParameters parameters)
        {
            EnsureMetadataSourceAvailable();
            try
            {
                RecordObservedInventory(parameters);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    $"Cannot record observed client inventory: {exception}");
            }

            return parameters.SkipSoftwareSync
                ? DoDriversSync(parameters)
                : DoSoftwareUpdateSync(parameters);
        }

        private List<MicrosoftUpdatePackageIdentity> GetUpdateIdentitiesFromClientIndexes(
            int[] clientIndexes)
        {
            var requested = clientIndexes ?? Array.Empty<int>();
            var identitiesByRevision = MetadataSource.GetPackageIdentities(requested);
            var identities = new List<MicrosoftUpdatePackageIdentity>(requested.Length);
            foreach (var revisionId in requested)
            {
                if (!identitiesByRevision.TryGetValue(revisionId, out var identity))
                {
                    throw new FaultException($"Revision ID not found: {revisionId}");
                }

                identities.Add(identity);
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
