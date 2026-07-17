// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Builds the client-sync Core XML fragment while metadata is imported.
    /// The fragment is persisted in SQLite so client scans do not have to parse
    /// and transform the complete update metadata repeatedly.
    /// </summary>
    internal static class ClientSyncCoreFragmentBuilder
    {
        private static readonly string[] AttributesToKeepInCoreFragment =
        {
            "UpdateType",
            "AutoSelectOnWebSites",
            "EulaID",
            "ExplicitlyDeployable",
            "OSUpgrade"
        };

        public static string Build(byte[] metadataBytes)
        {
            if (metadataBytes == null || metadataBytes.Length == 0)
            {
                throw new InvalidDataException("Package metadata is empty.");
            }

            using var stream = new MemoryStream(metadataBytes, writable: false);
            using var reader = new StreamReader(
                stream,
                Encoding.Unicode,
                detectEncodingFromByteOrderMarks: true);
            var xml = XDocument.Parse(reader.ReadToEnd(), LoadOptions.None);
            StripNamespaces(xml);

            var identity = xml.Root?.XPathSelectElement("/Update/UpdateIdentity")
                ?? throw new InvalidDataException("Update metadata is missing UpdateIdentity.");
            var properties = xml.Root.XPathSelectElement("/Update/Properties")
                ?? throw new InvalidDataException("Update metadata is missing Properties.");
            var filteredProperties = FilterElementAttributes(
                properties,
                AttributesToKeepInCoreFragment);
            var relationships = xml.Root.XPathSelectElement("/Update/Relationships");
            var applicabilityRules = xml.Root.XPathSelectElement("/Update/ApplicabilityRules");

            if (applicabilityRules != null)
            {
                RemoveDriverMetadataNodes(applicabilityRules);
            }

            return identity.ToString(SaveOptions.DisableFormatting)
                + filteredProperties.ToString(SaveOptions.DisableFormatting)
                + relationships?.ToString(SaveOptions.DisableFormatting)
                + applicabilityRules?.ToString(SaveOptions.DisableFormatting);
        }

        private static void RemoveDriverMetadataNodes(XElement element)
        {
            foreach (var driverMetadataElement in element
                .XPathSelectElements("/Update/ApplicabilityRules/Metadata/d.WindowsDriverMetaData")
                .ToList())
            {
                driverMetadataElement.RemoveNodes();
            }
        }

        private static XElement FilterElementAttributes(
            XElement element,
            string[] attributesToKeep)
        {
            var attributes = element.Attributes().ToList();
            element.RemoveAttributes();

            foreach (var attribute in attributes)
            {
                if (attributesToKeep.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                {
                    element.Add(attribute);
                }
            }

            return element;
        }

        private static void StripNamespaces(XDocument xml)
        {
            if (xml.Root == null)
            {
                throw new InvalidDataException("Update metadata has no root element.");
            }

            foreach (var element in xml.Root.DescendantsAndSelf())
            {
                var namespaceName = element.Name.Namespace.NamespaceName;
                if (string.Equals(
                    namespaceName,
                    "http://schemas.microsoft.com/msus/2002/12/BaseApplicabilityRules",
                    StringComparison.Ordinal))
                {
                    element.Name = $"b.{element.Name.LocalName}";
                }
                else if (string.Equals(
                    namespaceName,
                    "http://schemas.microsoft.com/msus/2002/12/MsiApplicabilityRules",
                    StringComparison.Ordinal))
                {
                    element.Name = $"m.{element.Name.LocalName}";
                }
                else if (string.Equals(
                    namespaceName,
                    "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/WindowsDriver",
                    StringComparison.Ordinal))
                {
                    element.Name = $"d.{element.Name.LocalName}";
                }
                else
                {
                    element.Name = element.Name.LocalName;
                }

                element.ReplaceAttributes(
                    element.Attributes()
                        .Where(attribute => !attribute.IsNamespaceDeclaration)
                        .Select(attribute => new XAttribute(
                            attribute.Name.LocalName,
                            attribute.Value)));
            }
        }
    }
}
