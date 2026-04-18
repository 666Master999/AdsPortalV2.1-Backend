using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AdsPortalV2.Services;

public sealed class CategoryGraph
{
    public Dictionary<int, int?> ParentById { get; init; } = new Dictionary<int, int?>();
    public Dictionary<int, string> PathById { get; init; } = new Dictionary<int, string>();
    public Dictionary<int, List<int>> ChildrenById { get; init; } = new Dictionary<int, List<int>>();
}

public sealed class CategoryService(AppDbContext db, IMemoryCache cache) : ICategoryService
{
    private const string SnapshotCacheKey = "category:snapshot:v1";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private sealed record CategorySnapshot(
        IReadOnlyCollection<Category> Categories,
        IReadOnlyDictionary<int, Category> CategoriesById,
        CategoryGraph Graph,
        IReadOnlyDictionary<int, IReadOnlyCollection<CategoryAttributeDto>> EffectiveAttributes,
        IReadOnlyCollection<CategoryTreeDto> Tree);

    public async Task<CategoryGraph> GetGraphAsync(CancellationToken cancellationToken = default)
        => (await GetSnapshotAsync(cancellationToken)).Graph;

    public async Task<IReadOnlyCollection<CategoryTreeDto>> GetTreeAsync(CancellationToken cancellationToken = default)
        => (await GetSnapshotAsync(cancellationToken)).Tree;

    public async Task<CategoryViewDto?> GetViewAsync(int categoryId, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        if (!snapshot.CategoriesById.TryGetValue(categoryId, out var category))
            return null;

        var path = snapshot.Graph.PathById.TryGetValue(categoryId, out var pathValue)
            ? pathValue.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var id) ? id : 0)
                .Where(id => id > 0)
                .ToList()
            : [];

        var children = snapshot.Graph.ChildrenById.TryGetValue(categoryId, out var childIds)
            ? childIds
                .Select(id => snapshot.CategoriesById[id])
                .Select(c => new CategoryDto(c.Id, c.Name, c.ParentId, c.IsLeaf, c.Path))
                .ToList()
            : [];

        var filters = await GetAttributesAsync(categoryId, onlyFilters: true, cancellationToken);
        return new CategoryViewDto(new CategoryDto(category.Id, category.Name, category.ParentId, category.IsLeaf, category.Path), path, children, filters);
    }

    public async Task<IReadOnlyCollection<CategoryAttributeDto>> GetAttributesAsync(int categoryId, bool onlyFilters = false, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        if (!snapshot.EffectiveAttributes.TryGetValue(categoryId, out var attributes))
            return [];

        return onlyFilters ? attributes.Where(a => a.IsFilter).ToList() : attributes;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyCollection<CategoryAttributeDto>>> GetAttributeLookupAsync(IEnumerable<int> categoryIds, bool onlyFilters = false, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var result = new Dictionary<string, List<CategoryAttributeDto>>(StringComparer.OrdinalIgnoreCase);

        foreach (var categoryId in categoryIds.Distinct())
        {
            if (!snapshot.EffectiveAttributes.TryGetValue(categoryId, out var attributes))
                continue;

            foreach (var attribute in onlyFilters ? attributes.Where(a => a.IsFilter) : attributes)
            {
                if (!result.TryGetValue(attribute.Slug, out var list))
                {
                    list = [];
                    result[attribute.Slug] = list;
                }

                list.Add(attribute);
            }
        }

        return result.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyCollection<CategoryAttributeDto>)kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyCollection<int>> GetPathIdsAsync(int categoryId, CancellationToken cancellationToken = default)
    {
        var graph = (await GetSnapshotAsync(cancellationToken)).Graph;
        if (!graph.PathById.TryGetValue(categoryId, out var path))
            return [];

        return path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToArray();
    }

    // ExpandCategoryIdsAsync removed: callers should use Graph.ChildrenById or Expand logic where needed.

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var categories = await db.Categories.ToListAsync(cancellationToken);
        var attributes = await db.CategoryAttributes
            .AsNoTracking()
            .Include(a => a.Options)
            .ToListAsync(cancellationToken);

        var graph = BuildGraph(categories);
        var effectiveAttributes = BuildEffectiveAttributes(categories, attributes, graph);

        foreach (var category in categories)
        {
            category.Path = graph.PathById.TryGetValue(category.Id, out var path) ? path : category.Id.ToString();
            category.IsLeaf = !graph.ChildrenById.TryGetValue(category.Id, out var children) || children.Count == 0;
        }

        await db.SaveChangesAsync(cancellationToken);

        var tree = BuildTree(categories, effectiveAttributes);

        var snapshot = new CategorySnapshot(categories, categories.ToDictionary(c => c.Id), graph, effectiveAttributes, tree);
        cache.Set(SnapshotCacheKey, snapshot, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl,
            Priority = CacheItemPriority.High
        });
    }

    private async Task<CategorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(SnapshotCacheKey, out CategorySnapshot? snapshot) && snapshot != null)
            return snapshot;

        await RebuildAsync(cancellationToken);
        return cache.Get<CategorySnapshot>(SnapshotCacheKey)!;
    }

    private static CategoryGraph BuildGraph(IReadOnlyCollection<Category> categories)
    {
        var parentById = categories.ToDictionary(c => c.Id, c => c.ParentId);
        var childrenById = categories.ToDictionary(c => c.Id, _ => new List<int>());
        var categoriesById = categories.ToDictionary(c => c.Id);
        var pathById = new Dictionary<int, string>();

        foreach (var category in categories)
        {
            if (category.ParentId.HasValue && childrenById.TryGetValue(category.ParentId.Value, out var children))
                children.Add(category.Id);
        }

        foreach (var list in childrenById.Values)
            list.Sort((left, right) => string.Compare(categoriesById[left].Name, categoriesById[right].Name, StringComparison.OrdinalIgnoreCase));

        string BuildPath(int categoryId, HashSet<int> visited)
        {
            if (pathById.TryGetValue(categoryId, out var cached))
                return cached;

            // Cycle detection: if we revisit a node, fallback to simple id path
            if (!visited.Add(categoryId))
                return categoryId.ToString();

            if (!parentById.TryGetValue(categoryId, out var parentId) || parentId is null)
                return pathById[categoryId] = categoryId.ToString();

            var result = $"{BuildPath(parentId.Value, visited)}/{categoryId}";
            pathById[categoryId] = result;
            return result;
        }

        foreach (var category in categories)
            BuildPath(category.Id, new HashSet<int>());

        return new CategoryGraph
        {
            ParentById = parentById,
            PathById = pathById,
            ChildrenById = childrenById
        };
    }

    private static IReadOnlyDictionary<int, IReadOnlyCollection<CategoryAttributeDto>> BuildEffectiveAttributes(
        IReadOnlyCollection<Category> categories,
        IReadOnlyCollection<CategoryAttribute> attributes,
        CategoryGraph graph)
    {
        var categoriesById = categories.ToDictionary(c => c.Id);
        var attributesByCategory = attributes
            .GroupBy(a => a.CategoryId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Name).ThenBy(a => a.Id).ToList());

        var result = new Dictionary<int, IReadOnlyCollection<CategoryAttributeDto>>();

        foreach (var category in categoriesById.Values)
        {
            if (!graph.PathById.TryGetValue(category.Id, out var path))
            {
                result[category.Id] = [];
                continue;
            }

            var merged = new Dictionary<string, CategoryAttributeDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var pathId in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var id) ? id : 0).Where(id => id > 0))
            {
                if (!attributesByCategory.TryGetValue(pathId, out var categoryAttributes))
                    continue;

                foreach (var attribute in categoryAttributes)
                {
                    merged[attribute.Slug] = new CategoryAttributeDto(
                        attribute.Id,
                        attribute.CategoryId,
                        attribute.Slug,
                        attribute.Name,
                        attribute.Type,
                        attribute.IsRequired,
                        attribute.IsFilter,
                        attribute.Options
                            .OrderBy(o => o.Id)
                            .Select(o => new CategoryAttributeOptionDto(o.Id, o.CategoryAttributeId, o.Value))
                            .ToList());
                }
            }

            result[category.Id] = merged.Values.OrderBy(a => a.Name).ToList();
        }

        return result;
    }

    private static IReadOnlyCollection<CategoryTreeDto> BuildTree(
        IReadOnlyCollection<Category> categories,
        IReadOnlyDictionary<int, IReadOnlyCollection<CategoryAttributeDto>> effectiveAttributes)
    {
        var byId = categories.ToDictionary(c => c.Id);
        var childrenById = categories
            .Where(c => c.ParentId.HasValue)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Name).Select(c => c.Id).ToList());
        var rootIds = categories
            .Where(c => c.ParentId is null)
            .OrderBy(c => c.Name)
            .Select(c => c.Id)
            .ToList();

        IReadOnlyCollection<CategoryTreeDto> BuildChildren(int? parentId)
        {
            var childIds = parentId is null
                ? rootIds
                : childrenById.TryGetValue(parentId.Value, out var existing)
                    ? existing
                    : [];

            if (childIds.Count == 0)
                return [];

            return childIds.Select(childId =>
            {
                var category = byId[childId];
                return new CategoryTreeDto(
                    category.Id,
                    category.Name,
                    category.ParentId,
                    category.IsLeaf,
                    category.Path,
                    effectiveAttributes.TryGetValue(category.Id, out var attrs) ? attrs : Array.Empty<CategoryAttributeDto>(),
                    BuildChildren(category.Id));
            }).ToList();
        }

        return BuildChildren(null);
    }
}
