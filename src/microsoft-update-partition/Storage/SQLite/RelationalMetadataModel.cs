// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Drivers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Canonical, normalized representation of the parts of Microsoft Update metadata
    /// that are required by the local package graph and by the client-sync endpoint.
    ///
    /// Arbitrary applicability expressions are deliberately kept as one protocol fragment:
    /// expanding the expression language into one SQLite row per XML node is larger than the
    /// original expression and provides no useful query surface. Driver metadata is removed
    /// from that fragment and stored relationally, then injected again when metadata is rendered.
    /// </summary>
    internal sealed class RelationalPackageRecord
    {
        public Guid UpdateId { get; set; }
        public int Revision { get; set; }
        public List<RelationalNameValue> PropertyAttributes { get; } = new();
        public List<RelationalElementValue> PropertyElements { get; } = new();
        public List<RelationalLocalizedElement> LocalizedElements { get; } = new();
        public List<RelationalRelationshipGroup> RelationshipGroups { get; } = new();
        public List<RelationalRelationshipItem> RelationshipItems { get; } = new();
        public List<string> RelationshipExtraElements { get; } = new();
        public string ApplicabilityTemplateXml { get; set; }
        public string HandlerSpecificXml { get; set; }
        public List<DriverMetadata> DriverMetadata { get; } = new();
        public List<UpdateFile> Files { get; } = new();
    }

    internal sealed class RelationalNameValue
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    internal class RelationalElementValue
    {
        public int Ordinal { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public string Xml { get; set; }
    }

    internal sealed class RelationalLocalizedElement : RelationalElementValue
    {
        public string Language { get; set; }
    }

    internal sealed class RelationalRelationshipGroup
    {
        public string RelationshipType { get; set; }
        public int GroupOrdinal { get; set; }
        public string GroupKind { get; set; }
        public bool IsCategory { get; set; }
    }

    internal sealed class RelationalRelationshipItem
    {
        public string RelationshipType { get; set; }
        public int GroupOrdinal { get; set; }
        public int ItemOrdinal { get; set; }
        public Guid UpdateId { get; set; }
        public int? RevisionNumber { get; set; }
    }

    internal static class RelationalMetadataExtractor
    {
        internal const string PrerequisitesRelationship = "Prerequisites";
        internal const string SupersededRelationship = "SupersededUpdates";
        internal const string BundledRelationship = "BundledUpdates";
        internal const string DirectGroup = "direct";
        internal const string AtLeastOneGroup = "at-least-one";

        public static RelationalPackageRecord Extract(MicrosoftUpdatePackage package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            using var metadataStream = package.GetMetadataStream()
                ?? throw new InvalidDataException($"Package {package.Id} does not expose metadata");
            var document = XDocument.Load(metadataStream, LoadOptions.PreserveWhitespace);
            var result = ExtractDocument(package.Id, document);

            // Parse driver metadata from the XDocument we already loaded. Calling
            // DriverUpdate.GetDriverMetadata() here would parse the same XML a second time for
            // the very large driver catalogue.
            ExtractDriverMetadata(document.Root, result);

            if (package.Files != null)
            {
                result.Files.AddRange(package.Files.OfType<UpdateFile>());
            }

            return result;
        }

        private static RelationalPackageRecord ExtractDocument(
            MicrosoftUpdatePackageIdentity identity,
            XDocument document)
        {
            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "Update", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Package {identity} has invalid Microsoft Update metadata");
            }

            var result = new RelationalPackageRecord
            {
                UpdateId = identity.ID,
                Revision = identity.Revision
            };

            ExtractProperties(root, result);
            ExtractLocalizedProperties(root, result);
            ExtractRelationships(root, result);
            ExtractApplicability(root, result);
            ExtractHandlerSpecificData(root, result);
            return result;
        }

        private static void ExtractDriverMetadata(XElement root, RelationalPackageRecord result)
        {
            var metadataNodes = root?
                .Descendants()
                .Where(element => element.Name.LocalName == "WindowsDriverMetaData")
                .ToList();
            if (metadataNodes == null || metadataNodes.Count == 0)
            {
                return;
            }

            foreach (var node in metadataNodes)
            {
                var date = DateTime.MinValue;
                var dateValue = AttributeValue(node, "DriverVerDate");
                if (!string.IsNullOrWhiteSpace(dateValue))
                {
                    DateTime.TryParseExact(
                        dateValue,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out date);
                }

                ulong version = 0;
                var versionValue = AttributeValue(node, "DriverVerVersion");
                if (!string.IsNullOrWhiteSpace(versionValue))
                {
                    try
                    {
                        version = DriverVersion.ParseVersionFromString(versionValue);
                    }
                    catch (Exception)
                    {
                        version = 0;
                    }
                }

                var featureScores = new List<DriverFeatureScore>();
                foreach (var featureScore in node.Elements()
                    .Where(element => element.Name.LocalName == "FeatureScore"))
                {
                    var scoreValue = AttributeValue(featureScore, "FeatureScore");
                    if (!byte.TryParse(
                            scoreValue,
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var score))
                    {
                        continue;
                    }

                    featureScores.Add(new DriverFeatureScore
                    {
                        OperatingSystem = AttributeValue(featureScore, "OperatingSystem"),
                        Score = score
                    });
                }

                var distributionIds = ParseHardwareIds(node, "DistributionComputerHardwareId");
                var targetIds = ParseHardwareIds(node, "TargetComputerHardwareId");
                result.DriverMetadata.Add(new DriverMetadata(
                    AttributeValue(node, "HardwareID"),
                    AttributeValue(node, "WhqlDriverID"),
                    AttributeValue(node, "Manufacturer"),
                    AttributeValue(node, "Company"),
                    AttributeValue(node, "Provider"),
                    date,
                    version,
                    AttributeValue(node, "Class"),
                    featureScores,
                    distributionIds,
                    targetIds));
            }
        }

        private static List<Guid> ParseHardwareIds(XElement node, string localName)
        {
            var result = new List<Guid>();
            foreach (var element in node.Elements().Where(element => element.Name.LocalName == localName))
            {
                if (Guid.TryParse(element.Value, out var hardwareId))
                {
                    result.Add(hardwareId);
                }
            }

            return result;
        }

        private static string AttributeValue(XElement element, string localName)
        {
            return element.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == localName)?
                .Value;
        }
    }
}
