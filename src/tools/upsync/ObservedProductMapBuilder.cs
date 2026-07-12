// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using Microsoft.PackageGraph.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    internal sealed class ObservedProductMapBuildResult
    {
        public int ConcreteProductCount { get; }

        public int DetectoidCount { get; }

        public int MappingCount { get; }

        public int DetectoidCategoryMappingCount { get; }

        public int ProductPrerequisiteMappingCount { get; }

        public int SkippedPackageCount { get; }

        public ObservedProductMapBuildResult(
            int concreteProductCount,
            int detectoidCount,
            int mappingCount,
            int detectoidCategoryMappingCount,
            int productPrerequisiteMappingCount,
            int skippedPackageCount)
        {
            ConcreteProductCount = concreteProductCount;
            DetectoidCount = detectoidCount;
            MappingCount = mappingCount;
            DetectoidCategoryMappingCount = detectoidCategoryMappingCount;
            ProductPrerequisiteMappingCount = productPrerequisiteMappingCount;
            SkippedPackageCount = skippedPackageCount;
        }
    }

    /// <summary>
    /// Builds a conservative detectoid-to-product map from category metadata
    /// already stored by pre-fetch. Generic software-update prerequisites are
    /// deliberately ignored because architecture/version detectoids are shared
    /// by many unrelated products.
    /// </summary>
    internal static class ObservedProductMapBuilder
    {
        public static bool IsSupported(IMetadataStore store) => store is IObservedInventoryStore;

        public static ObservedProductMapBuildResult Rebuild(IMetadataStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (store is not IObservedInventoryStore observedInventoryStore)
            {
                throw new InvalidOperationException(
                    "The selected metadata store cannot persist the detectoid-to-product map. " +
                    "Use a local SQLite store.");
            }

            var skippedPackages = 0;
            var products = new List<ProductCategory>();
            foreach (var product in SelectLatestRevisions(store.OfType<ProductCategory>()))
            {
                try
                {
                    if (IsConcreteProduct(product))
                    {
                        products.Add(product);
                    }
                }
                catch (Exception ex)
                {
                    skippedPackages++;
                    ConsoleOutput.WriteRed(
                        $"Warning: cannot inspect product category {product.Id}: {ex.Message}");
                }
            }
            var detectoids = SelectLatestRevisions(store.OfType<DetectoidCategory>())
                .ToList();

            var productIds = products
                .Select(product => product.Id.ID)
                .ToHashSet();
            var detectoidIds = detectoids
                .Select(detectoid => detectoid.Id.ID)
                .ToHashSet();

            var mappings = new Dictionary<(Guid DetectoidId, Guid ProductId), DetectoidProductMappingSource>();

            // Source 1: the detectoid itself explicitly carries a concrete
            // product category in an IsCategory prerequisite group.
            foreach (var detectoid in detectoids)
            {
                try
                {
                    foreach (var productId in (detectoid.Categories ?? Array.Empty<Guid>())
                        .Where(productIds.Contains))
                    {
                        AddMapping(
                            mappings,
                            detectoid.Id.ID,
                            productId,
                            DetectoidProductMappingSource.DetectoidCategory);
                    }
                }
                catch (Exception ex)
                {
                    skippedPackages++;
                    ConsoleOutput.WriteRed(
                        $"Warning: cannot inspect detectoid categories {detectoid.Id}: {ex.Message}");
                }
            }

            // Source 2: a concrete product category explicitly names a detectoid
            // as a real (non-category) prerequisite. Do not infer mappings from
            // ordinary software updates: shared detectoids would broaden the
            // observed product set aggressively.
            foreach (var product in products)
            {
                try
                {
                    foreach (var detectoidId in EnumerateNonCategoryPrerequisiteIds(product.Prerequisites)
                        .Where(detectoidIds.Contains))
                    {
                        AddMapping(
                            mappings,
                            detectoidId,
                            product.Id.ID,
                            DetectoidProductMappingSource.ProductPrerequisite);
                    }
                }
                catch (Exception ex)
                {
                    skippedPackages++;
                    ConsoleOutput.WriteRed(
                        $"Warning: cannot inspect product prerequisites {product.Id}: {ex.Message}");
                }
            }

            var persistedMappings = mappings
                .Select(pair => new DetectoidProductMapping(
                    pair.Key.DetectoidId,
                    pair.Key.ProductId,
                    pair.Value))
                .ToList();

            observedInventoryStore.ReplaceDetectoidProductMappings(
                persistedMappings,
                DateTimeOffset.UtcNow);

            return new ObservedProductMapBuildResult(
                products.Count,
                detectoids.Count,
                persistedMappings.Count,
                persistedMappings.Count(mapping =>
                    mapping.Source.HasFlag(DetectoidProductMappingSource.DetectoidCategory)),
                persistedMappings.Count(mapping =>
                    mapping.Source.HasFlag(DetectoidProductMappingSource.ProductPrerequisite)),
                skippedPackages);
        }

        private static List<T> SelectLatestRevisions<T>(IEnumerable<T> packages)
            where T : MicrosoftUpdatePackage
        {
            return (packages ?? Array.Empty<T>())
                .GroupBy(package => package.Id.ID)
                .Select(group => group
                    .OrderByDescending(package => package.Id.Revision)
                    .First())
                .ToList();
        }

        internal static bool IsConcreteProduct(ProductCategory product)
        {
            if (product == null)
            {
                return false;
            }

            using var metadataStream = product.GetMetadataStream();
            if (metadataStream == null)
            {
                return false;
            }

            var document = XDocument.Load(metadataStream, LoadOptions.None);
            const string categoryNamespace =
                "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/Category";

            var categoryInformation = document
                .Descendants()
                .FirstOrDefault(element =>
                    string.Equals(
                        element.Name.LocalName,
                        "CategoryInformation",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        element.Name.NamespaceName,
                        categoryNamespace,
                        StringComparison.Ordinal));
            var categoryType = categoryInformation?
                .Attributes()
                .FirstOrDefault(attribute =>
                    string.Equals(
                        attribute.Name.LocalName,
                        "CategoryType",
                        StringComparison.Ordinal))?
                .Value;

            return string.Equals(
                categoryType,
                "Product",
                StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<Guid> EnumerateNonCategoryPrerequisiteIds(
            IEnumerable<IPrerequisite> prerequisites)
        {
            foreach (var prerequisite in prerequisites ?? Array.Empty<IPrerequisite>())
            {
                switch (prerequisite)
                {
                    case Simple simple:
                        yield return simple.UpdateId;
                        break;

                    case AtLeastOne atLeastOne when !atLeastOne.IsCategory:
                        foreach (var simplePrerequisite in atLeastOne.Simple)
                        {
                            yield return simplePrerequisite.UpdateId;
                        }
                        break;
                }
            }
        }

        private static void AddMapping(
            IDictionary<(Guid DetectoidId, Guid ProductId), DetectoidProductMappingSource> mappings,
            Guid detectoidId,
            Guid productId,
            DetectoidProductMappingSource source)
        {
            if (detectoidId == Guid.Empty || productId == Guid.Empty)
            {
                return;
            }

            var key = (detectoidId, productId);
            mappings.TryGetValue(key, out var existingSource);
            mappings[key] = existingSource | source;
        }
    }
}
