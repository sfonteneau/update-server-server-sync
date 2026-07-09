// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Compression;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.UpdateServices.WebServices.ServerSync;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Source
{
    abstract class InMemoryUpdateFactory
    {
        internal static MicrosoftUpdatePackage FromServerSyncData(ServerSyncUpdateData serverSyncData, Dictionary<string, UpdateFileUrl> filesCollection)
        {
            return FromServerSyncData(serverSyncData, filesCollection, null);
        }

        internal static MicrosoftUpdatePackage FromServerSyncData(ServerSyncUpdateData serverSyncData, Dictionary<string, UpdateFileUrl> filesCollection, UpstreamSourceFilter sourceFilter)
        {
            byte[] metadata;
            if (!string.IsNullOrEmpty(serverSyncData.XmlUpdateBlob))
            {
                var compressedStream = new MemoryStream();
                using (var compressor = new GZipStream(compressedStream, CompressionLevel.Fastest, true))
                {
                    new MemoryStream(
                        Encoding.Unicode.GetBytes(serverSyncData.XmlUpdateBlob), false)
                        .CopyTo(compressor);
                }

                metadata = compressedStream.ToArray();
            }
            else
            {
                // If the plain text blob is not availabe, use the compressed XML blob
                if (serverSyncData.XmlUpdateBlobCompressed == null || serverSyncData.XmlUpdateBlobCompressed.Length == 0)
                {
                    throw new Exception("Missing XmlUpdateBlobCompressed");
                }

                // This call will throw an exception if a decompressor is not available for the current platform.
                metadata = CabinetUtility.RecompressUnicodeData(serverSyncData.XmlUpdateBlobCompressed);
            }

            if (sourceFilter?.StripUnrequestedLocalizedProperties == true && sourceFilter.LanguagesFilter?.Count > 0)
            {
                metadata = StripUnrequestedLocalizedProperties(metadata, sourceFilter.LanguagesFilter);
            }

            return MicrosoftUpdatePackage.FromMetadataXml(metadata, filesCollection);
        }

        private static byte[] StripUnrequestedLocalizedProperties(byte[] gzippedMetadata, IEnumerable<int> languageIds)
        {
            var allowedLanguages = BuildAllowedLanguageNames(languageIds);
            if (allowedLanguages.Count == 0)
            {
                return gzippedMetadata;
            }

            byte[] xmlBytes;
            using (var input = new GZipStream(new MemoryStream(gzippedMetadata, false), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                input.CopyTo(output);
                xmlBytes = output.ToArray();
            }

            XDocument document;
            using (var xmlStream = new MemoryStream(xmlBytes, false))
            {
                document = XDocument.Load(xmlStream, LoadOptions.PreserveWhitespace);
            }

            var localizedProperties = document
                .Descendants()
                .Where(element => element.Name.LocalName == "LocalizedProperties")
                .ToList();

            if (localizedProperties.Count == 0)
            {
                return gzippedMetadata;
            }

            var toRemove = new List<XElement>();
            foreach (var localizedProperty in localizedProperties)
            {
                var language = localizedProperty
                    .Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "Language")?
                    .Value?
                    .Trim();

                if (string.IsNullOrEmpty(language))
                {
                    continue;
                }

                if (!allowedLanguages.Contains(language.ToLowerInvariant()))
                {
                    toRemove.Add(localizedProperty);
                }
            }

            if (toRemove.Count == 0 || toRemove.Count == localizedProperties.Count)
            {
                // Never create metadata with an empty LocalizedPropertiesCollection; that would break title parsing.
                return gzippedMetadata;
            }

            foreach (var element in toRemove)
            {
                element.Remove();
            }

            byte[] rewrittenXmlBytes;
            using (var rewrittenXml = new MemoryStream())
            {
                document.Save(rewrittenXml, SaveOptions.DisableFormatting);
                rewrittenXmlBytes = rewrittenXml.ToArray();
            }

            using (var compressedStream = new MemoryStream())
            {
                using (var compressor = new GZipStream(compressedStream, CompressionLevel.Fastest, true))
                {
                    compressor.Write(rewrittenXmlBytes, 0, rewrittenXmlBytes.Length);
                }

                return compressedStream.ToArray();
            }
        }

        private static HashSet<string> BuildAllowedLanguageNames(IEnumerable<int> languageIds)
        {
            var allowedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // English is the hard fallback expected by the metadata parser and by Windows Update protocol behavior.
                "en",
                "en-us"
            };

            foreach (var languageId in languageIds.Distinct())
            {
                try
                {
                    var culture = CultureInfo.GetCultureInfo(languageId);
                    if (!string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
                    {
                        allowedLanguages.Add(culture.TwoLetterISOLanguageName.ToLowerInvariant());
                    }

                    if (!string.IsNullOrEmpty(culture.Name))
                    {
                        allowedLanguages.Add(culture.Name.ToLowerInvariant());
                    }
                }
                catch (CultureNotFoundException)
                {
                    if (languageId == 1033)
                    {
                        allowedLanguages.Add("en");
                        allowedLanguages.Add("en-us");
                    }
                }
            }

            return allowedLanguages;
        }

        internal static UpdateFileUrl FromServerSyncData(ServerSyncUrlData urlData)
        {
            return new UpdateFileUrl(Convert.ToBase64String(urlData.FileDigest), urlData.MUUrl, urlData.UssUrl);
        }
    }
}
