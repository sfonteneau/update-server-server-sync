// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Handlers;
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

        public int CategoryHierarchyMappingCount { get; }

        public int TransitiveProductPrerequisiteMappingCount { get; }

        public int ObservedProductCategoryMappingCount { get; }

        public int AmbiguousDetectoidCount { get; }

        public int SkippedPackageCount { get; }

        public ObservedProductMapBuildResult(
            int concreteProductCount,
            int detectoidCount,
            int mappingCount,
            int detectoidCategoryMappingCount,
            int productPrerequisiteMappingCount,
            int categoryHierarchyMappingCount,
            int transitiveProductPrerequisiteMappingCount,
            int observedProductCategoryMappingCount,
            int ambiguousDetectoidCount,
            int skippedPackageCount)
        {
            ConcreteProductCount = concreteProductCount;
            DetectoidCount = detectoidCount;
            MappingCount = mappingCount;
            DetectoidCategoryMappingCount = detectoidCategoryMappingCount;
            ProductPrerequisiteMappingCount = productPrerequisiteMappingCount;
            CategoryHierarchyMappingCount = categoryHierarchyMappingCount;
            TransitiveProductPrerequisiteMappingCount = transitiveProductPrerequisiteMappingCount;
            ObservedProductCategoryMappingCount = observedProductCategoryMappingCount;
            AmbiguousDetectoidCount = ambiguousDetectoidCount;
            SkippedPackageCount = skippedPackageCount;
        }
    }

    /// <summary>
    /// Builds a deliberately strict detectoid-to-product map from the category and
    /// detectoid metadata cached by pre-fetch.
    ///
    /// Only direct detectoid evidence is accepted:
    /// - a detectoid directly declares one concrete Product category;
    /// - one concrete Product directly declares the detectoid as a prerequisite.
    ///
    /// Product-category nodes reported in InstalledNonLeafUpdateIDs are not trusted
    /// on their own: real WUA scans can report broad catalog category nodes that do
    /// not prove the corresponding product is installed.
    ///
    /// Product-family expansion and transitive prerequisite traversal are excluded.
    /// If the remaining direct evidence points to more than one concrete product,
    /// the detector is ambiguous and no mapping is persisted for it.
    /// </summary>
    internal static class ObservedProductMapBuilder
    {
        /// <summary>
        /// Stored with the derived map so deployments automatically replace maps
        /// produced by an older, broader inference algorithm.
        /// </summary>
        public const string MappingAlgorithmVersion = "strict-detectoid-direct-v3";

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
            var productCategories = SelectLatestRevisions(store.OfType<ProductCategory>());
            var concreteProducts = new List<ProductCategory>();
            foreach (var product in productCategories)
            {
                try
                {
                    if (IsConcreteProduct(product))
                    {
                        concreteProducts.Add(product);
                    }
                }
                catch (Exception ex)
                {
                    skippedPackages++;
                    ConsoleOutput.WriteRed(
                        $"Warning: cannot inspect product category {product.Id}: {ex.Message}");
                }
            }

            var detectoids = SelectLatestRevisions(store.OfType<DetectoidCategory>());
            var concreteProductIds = concreteProducts
                .Select(product => product.Id.ID)
                .ToHashSet();
            var detectoidIds = detectoids
                .Select(detectoid => detectoid.Id.ID)
                .ToHashSet();

            var candidates = new Dictionary<
                Guid,
                Dictionary<Guid, DetectoidProductMappingSource>>();

            // A detectoid may directly declare a concrete Product category. Ignore
            // ProductFamily/Company categories: expanding those nodes is what caused
            // generic detectoids to select large numbers of unrelated products.
            foreach (var detectoid in detectoids)
            {
                try
                {
                    foreach (var productId in EnumerateCategoryPrerequisiteIds(detectoid.Prerequisites)
                        .Where(concreteProductIds.Contains)
                        .Distinct())
                    {
                        AddCandidate(
                            candidates,
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

            // Accept only direct product prerequisites. Do not walk through an
            // intermediate detectoid graph: architecture, OS and shared runtime
            // detectoids are commonly reused by many unrelated products.
            foreach (var product in concreteProducts)
            {
                try
                {
                    foreach (var detectoidId in EnumerateNonCategoryPrerequisiteIds(product.Prerequisites)
                        .Where(detectoidIds.Contains)
                        .Distinct())
                    {
                        AddCandidate(
                            candidates,
                            detectoidId,
                            product.Id.ID,
                            DetectoidProductMappingSource.ProductPrerequisite);
                    }
                }
                catch (Exception ex)
                {
                    skippedPackages++;
                    ConsoleOutput.WriteRed(
                        $"Warning: cannot inspect direct product prerequisites {product.Id}: {ex.Message}");
                }
            }

            var mappings = new List<DetectoidProductMapping>();
            var ambiguousDetectoids = new HashSet<Guid>();
            foreach (var detector in candidates)
            {
                // All accepted direct evidence must converge on exactly one
                // concrete product. A detector shared by two products is ignored,
                // even if one path appears stronger than another.
                if (detector.Value.Count != 1)
                {
                    ambiguousDetectoids.Add(detector.Key);
                    continue;
                }

                var candidate = detector.Value.Single();
                mappings.Add(new DetectoidProductMapping(
                    detector.Key,
                    candidate.Key,
                    candidate.Value));
            }

            observedInventoryStore.ReplaceDetectoidProductMappings(
                mappings,
                DateTimeOffset.UtcNow,
                MappingAlgorithmVersion);

            return new ObservedProductMapBuildResult(
                concreteProducts.Count,
                detectoids.Count,
                mappings.Count,
                mappings.Count(mapping =>
                    mapping.Source.HasFlag(DetectoidProductMappingSource.DetectoidCategory)),
                mappings.Count(mapping =>
                    mapping.Source.HasFlag(DetectoidProductMappingSource.ProductPrerequisite)),
                0,
                0,
                mappings.Count(mapping =>
                    mapping.Source.HasFlag(DetectoidProductMappingSource.ObservedProductCategory)),
                ambiguousDetectoids.Count,
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
            return string.Equals(
                GetCategoryType(product),
                "Product",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetCategoryType(ProductCategory product)
        {
            if (product == null)
            {
                return null;
            }

            // Prefer the normal metadata parser. Keep the XML fallback because
            // older/custom metadata stores may not expose Handler through an index.
            try
            {
                if (product.Handler is CategoryHandler categoryHandler
                    && !string.IsNullOrWhiteSpace(categoryHandler.CategoryType))
                {
                    return categoryHandler.CategoryType;
                }
            }
            catch
            {
                // The XML fallback below gives the map builder a second chance.
            }

            using var metadataStream = product.GetMetadataStream();
            if (metadataStream == null)
            {
                return null;
            }

            var document = XDocument.Load(metadataStream, LoadOptions.None);
            const string categoryNamespace =
                "http://schemas.microsoft.com/msus/2002/12/UpdateHandlers/Category";

            var categoryInformation = document
                .Descendants()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "CategoryInformation", StringComparison.Ordinal)
                    && string.Equals(element.Name.NamespaceName, categoryNamespace, StringComparison.Ordinal))
                ?? document
                    .Descendants()
                    .FirstOrDefault(element =>
                        string.Equals(element.Name.LocalName, "CategoryInformation", StringComparison.Ordinal));

            return categoryInformation?
                .Attributes()
                .FirstOrDefault(attribute =>
                    string.Equals(attribute.Name.LocalName, "CategoryType", StringComparison.OrdinalIgnoreCase))?
                .Value;
        }

        private static IEnumerable<Guid> EnumerateCategoryPrerequisiteIds(
            IEnumerable<IPrerequisite> prerequisites)
        {
            foreach (var prerequisite in prerequisites ?? Array.Empty<IPrerequisite>())
            {
                if (prerequisite is not AtLeastOne atLeastOne || !atLeastOne.IsCategory)
                {
                    continue;
                }

                foreach (var simple in atLeastOne.Simple ?? Enumerable.Empty<Simple>())
                {
                    if (simple.UpdateId != Guid.Empty)
                    {
                        yield return simple.UpdateId;
                    }
                }
            }
        }

        private static IEnumerable<Guid> EnumerateNonCategoryPrerequisiteIds(
            IEnumerable<IPrerequisite> prerequisites)
        {
            foreach (var prerequisite in prerequisites ?? Array.Empty<IPrerequisite>())
            {
                switch (prerequisite)
                {
                    case Simple simple when simple.UpdateId != Guid.Empty:
                        yield return simple.UpdateId;
                        break;

                    case AtLeastOne atLeastOne when !atLeastOne.IsCategory:
                        foreach (var simplePrerequisite in atLeastOne.Simple
                            ?? Enumerable.Empty<Simple>())
                        {
                            if (simplePrerequisite.UpdateId != Guid.Empty)
                            {
                                yield return simplePrerequisite.UpdateId;
                            }
                        }
                        break;
                }
            }
        }

        private static void AddCandidate(
            IDictionary<Guid, Dictionary<Guid, DetectoidProductMappingSource>> candidates,
            Guid detectorId,
            Guid productId,
            DetectoidProductMappingSource source)
        {
            if (detectorId == Guid.Empty
                || productId == Guid.Empty
                || source == DetectoidProductMappingSource.None)
            {
                return;
            }

            if (!candidates.TryGetValue(detectorId, out var products))
            {
                products = new Dictionary<Guid, DetectoidProductMappingSource>();
                candidates[detectorId] = products;
            }

            products.TryGetValue(productId, out var existingSource);
            products[productId] = existingSource | source;
        }
    }
}
