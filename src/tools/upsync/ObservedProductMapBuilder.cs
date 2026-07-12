// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Handlers;
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
    /// Builds a conservative detectoid-to-product map from the category and
    /// detectoid metadata cached by pre-fetch.
    ///
    /// Microsoft Update frequently links a product to a detectoid through one or
    /// more intermediate detectoids or through the product-category hierarchy.
    /// A direct-only lookup therefore misses real products. This builder follows
    /// both graphs, but discards indirect detectoids that fan out to too many
    /// equally-near products so generic architecture/version detectoids do not
    /// cause a broad catalog synchronization.
    /// </summary>
    internal static class ObservedProductMapBuilder
    {
        private const int MaxTraversalDepth = 32;
        private const int MaxProductsPerIndirectDetectoid = 16;

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
            var productCategoryIds = productCategories
                .Select(product => product.Id.ID)
                .ToHashSet();
            var detectoidIds = detectoids
                .Select(detectoid => detectoid.Id.ID)
                .ToHashSet();

            var mappings = new Dictionary<
                (Guid DetectoidId, Guid ProductId),
                DetectoidProductMappingSource>();
            var ambiguousDetectoids = new HashSet<Guid>();

            // Build the product category tree. An IsCategory prerequisite on a
            // category is its parent company/product-family/product category.
            var categoryChildren = new Dictionary<Guid, HashSet<Guid>>();
            foreach (var category in productCategories)
            {
                try
                {
                    foreach (var parentId in EnumerateCategoryPrerequisiteIds(category.Prerequisites)
                        .Where(productCategoryIds.Contains))
                    {
                        AddGraphEdge(categoryChildren, parentId, category.Id.ID);
                    }
                }
                catch (Exception ex)
                {
                    skippedPackages++;
                    ConsoleOutput.WriteRed(
                        $"Warning: cannot inspect product category hierarchy {category.Id}: {ex.Message}");
                }
            }

            var concreteDescendantCache = new Dictionary<Guid, Dictionary<Guid, int>>();
            Dictionary<Guid, int> GetConcreteDescendants(Guid categoryId)
            {
                if (concreteDescendantCache.TryGetValue(categoryId, out var cached))
                {
                    return cached;
                }

                var result = new Dictionary<Guid, int>();
                var bestDepth = new Dictionary<Guid, int>();
                var queue = new Queue<(Guid Id, int Depth)>();
                queue.Enqueue((categoryId, 0));
                bestDepth[categoryId] = 0;

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (current.Depth > MaxTraversalDepth)
                    {
                        continue;
                    }

                    if (concreteProductIds.Contains(current.Id))
                    {
                        if (!result.TryGetValue(current.Id, out var existingDepth)
                            || current.Depth < existingDepth)
                        {
                            result[current.Id] = current.Depth;
                        }
                    }

                    if (!categoryChildren.TryGetValue(current.Id, out var children))
                    {
                        continue;
                    }

                    foreach (var child in children)
                    {
                        var childDepth = current.Depth + 1;
                        if (bestDepth.TryGetValue(child, out var knownDepth)
                            && knownDepth <= childDepth)
                        {
                            continue;
                        }

                        bestDepth[child] = childDepth;
                        queue.Enqueue((child, childDepth));
                    }
                }

                concreteDescendantCache[categoryId] = result;
                return result;
            }

            // WUA can report product-category nodes directly in
            // InstalledNonLeafUpdateIDs. Map a concrete product to itself and a
            // family/company node to its nearest concrete product descendants.
            // These rows share the observed_detectoids table deliberately: it is
            // an aggregate set of product detectors, not client provenance.
            foreach (var category in productCategories)
            {
                var descendants = GetConcreteDescendants(category.Id.ID);
                if (descendants.Count == 0)
                {
                    continue;
                }

                var minimumDepth = descendants.Values.Min();
                var nearestProducts = descendants
                    .Where(pair => pair.Value == minimumDepth)
                    .Select(pair => pair.Key)
                    .ToList();
                if (minimumDepth > 0
                    && nearestProducts.Count > MaxProductsPerIndirectDetectoid)
                {
                    ambiguousDetectoids.Add(category.Id.ID);
                    continue;
                }

                var source = minimumDepth == 0
                    ? DetectoidProductMappingSource.ObservedProductCategory
                    : DetectoidProductMappingSource.CategoryHierarchy;
                foreach (var productId in nearestProducts)
                {
                    AddMapping(mappings, category.Id.ID, productId, source);
                }
            }

            // Source 1: explicit category membership. When the category is a
            // product family/company, use only the nearest concrete descendants
            // and reject very broad fan-out.
            foreach (var detectoid in detectoids)
            {
                try
                {
                    foreach (var categoryId in EnumerateCategoryPrerequisiteIds(detectoid.Prerequisites))
                    {
                        var descendants = GetConcreteDescendants(categoryId);
                        if (descendants.Count == 0)
                        {
                            continue;
                        }

                        var minimumDepth = descendants.Values.Min();
                        var nearestProducts = descendants
                            .Where(pair => pair.Value == minimumDepth)
                            .Select(pair => pair.Key)
                            .ToList();

                        if (minimumDepth > 0
                            && nearestProducts.Count > MaxProductsPerIndirectDetectoid)
                        {
                            ambiguousDetectoids.Add(detectoid.Id.ID);
                            continue;
                        }

                        var source = minimumDepth == 0
                            ? DetectoidProductMappingSource.DetectoidCategory
                            : DetectoidProductMappingSource.CategoryHierarchy;
                        foreach (var productId in nearestProducts)
                        {
                            AddMapping(mappings, detectoid.Id.ID, productId, source);
                        }
                    }
                }
                catch (Exception ex)
                {
                    skippedPackages++;
                    ConsoleOutput.WriteRed(
                        $"Warning: cannot inspect detectoid categories {detectoid.Id}: {ex.Message}");
                }
            }

            // Build a prerequisite graph from products and detectoids. Starting
            // from each concrete product and walking towards its prerequisites
            // identifies both direct and transitive detectoids used for product
            // detection.
            var prerequisitesByNode = new Dictionary<Guid, List<Guid>>();
            foreach (var package in detectoids
                .Cast<MicrosoftUpdatePackage>()
                .Concat(productCategories))
            {
                try
                {
                    prerequisitesByNode[package.Id.ID] = EnumerateNonCategoryPrerequisiteIds(
                            package.Prerequisites)
                        .Distinct()
                        .ToList();
                }
                catch (Exception ex)
                {
                    skippedPackages++;
                    ConsoleOutput.WriteRed(
                        $"Warning: cannot inspect prerequisites for {package.Id}: {ex.Message}");
                }
            }

            var candidateProductsByDetectoid = new Dictionary<Guid, Dictionary<Guid, int>>();
            foreach (var product in concreteProducts)
            {
                var bestDepth = new Dictionary<Guid, int>();
                var queue = new Queue<(Guid Id, int Depth)>();
                queue.Enqueue((product.Id.ID, 0));
                bestDepth[product.Id.ID] = 0;

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (current.Depth >= MaxTraversalDepth
                        || !prerequisitesByNode.TryGetValue(current.Id, out var prerequisites))
                    {
                        continue;
                    }

                    foreach (var prerequisiteId in prerequisites)
                    {
                        var depth = current.Depth + 1;
                        if (detectoidIds.Contains(prerequisiteId))
                        {
                            AddCandidate(
                                candidateProductsByDetectoid,
                                prerequisiteId,
                                product.Id.ID,
                                depth);
                        }

                        if (!prerequisitesByNode.ContainsKey(prerequisiteId))
                        {
                            continue;
                        }

                        if (bestDepth.TryGetValue(prerequisiteId, out var knownDepth)
                            && knownDepth <= depth)
                        {
                            continue;
                        }

                        bestDepth[prerequisiteId] = depth;
                        queue.Enqueue((prerequisiteId, depth));
                    }
                }
            }

            foreach (var detectoidCandidates in candidateProductsByDetectoid)
            {
                var minimumDepth = detectoidCandidates.Value.Values.Min();
                var nearestProducts = detectoidCandidates.Value
                    .Where(pair => pair.Value == minimumDepth)
                    .Select(pair => pair.Key)
                    .ToList();

                if (nearestProducts.Count > MaxProductsPerIndirectDetectoid)
                {
                    ambiguousDetectoids.Add(detectoidCandidates.Key);
                    continue;
                }

                var source = minimumDepth == 1
                    ? DetectoidProductMappingSource.ProductPrerequisite
                    : DetectoidProductMappingSource.TransitiveProductPrerequisite;
                foreach (var productId in nearestProducts)
                {
                    AddMapping(mappings, detectoidCandidates.Key, productId, source);
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
                concreteProducts.Count,
                detectoids.Count,
                persistedMappings.Count,
                persistedMappings.Count(mapping =>
                    mapping.Source.HasFlag(DetectoidProductMappingSource.DetectoidCategory)),
                persistedMappings.Count(mapping =>
                    mapping.Source.HasFlag(DetectoidProductMappingSource.ProductPrerequisite)),
                persistedMappings.Count(mapping =>
                    mapping.Source.HasFlag(DetectoidProductMappingSource.CategoryHierarchy)),
                persistedMappings.Count(mapping =>
                    mapping.Source.HasFlag(DetectoidProductMappingSource.TransitiveProductPrerequisite)),
                persistedMappings.Count(mapping =>
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

        private static void AddGraphEdge(
            IDictionary<Guid, HashSet<Guid>> graph,
            Guid source,
            Guid target)
        {
            if (source == Guid.Empty || target == Guid.Empty || source == target)
            {
                return;
            }

            if (!graph.TryGetValue(source, out var targets))
            {
                targets = new HashSet<Guid>();
                graph[source] = targets;
            }

            targets.Add(target);
        }

        private static void AddCandidate(
            IDictionary<Guid, Dictionary<Guid, int>> candidates,
            Guid detectoidId,
            Guid productId,
            int depth)
        {
            if (!candidates.TryGetValue(detectoidId, out var products))
            {
                products = new Dictionary<Guid, int>();
                candidates[detectoidId] = products;
            }

            if (!products.TryGetValue(productId, out var existingDepth)
                || depth < existingDepth)
            {
                products[productId] = depth;
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
