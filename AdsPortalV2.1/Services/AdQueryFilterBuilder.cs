using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AdsPortalV2.Services;

public sealed class AdQueryFilterBuilder(AppDbContext db, IMemoryCache cache)
{
    public async Task<(IQueryable<Ad> Query, HashSet<int>? ExpandedLocations)> BuildBaseQueryAsync(
        IQueryable<Ad> query,
        AdsQuery q,
        int[] locationIds,
        int[] categoryIds,
        IReadOnlyCollection<AdAttributeFilterDto> attributeFilters,
        AdStatus? status,
        CancellationToken cancellationToken)
    {
        HashSet<int>? expandedLocations = null;
        if (locationIds.Length > 0)
            expandedLocations = await ExpandLocationIdsAsync(locationIds, cancellationToken);

        query = query.AsNoTracking();

        if (expandedLocations is { Count: > 0 })
            query = query.Where(x => expandedLocations.Contains(x.LocationId));

        if (categoryIds.Length > 0)
            query = query.Where(x => x.CategoryId.HasValue && categoryIds.Contains(x.CategoryId.Value));

        foreach (var filter in attributeFilters)
        {
            var attributeIds = filter.AttributeIds;
            var filterValues = filter.Values;
            query = query.Where(x => x.AttributeValues.Any(v => attributeIds.Contains(v.AttributeId) && filterValues.Contains(v.Value)));
        }

        if (!string.IsNullOrWhiteSpace(q.Type))
            query = query.Where(x => x.ListingType == q.Type);

        if (q.PriceFrom.HasValue)
            query = query.Where(x => x.Price >= q.PriceFrom.Value);

        if (q.PriceTo.HasValue)
            query = query.Where(x => x.Price <= q.PriceTo.Value);

        if (q.DateFrom.HasValue)
            query = query.Where(x => x.CreatedAt >= q.DateFrom.Value.ToDateTime(TimeOnly.MinValue));

        if (q.DateTo.HasValue)
            query = query.Where(x => x.CreatedAt < q.DateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));

        if (q.UserId.HasValue)
            query = query.Where(x => x.UserId == q.UserId.Value);

        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);

        return (query, expandedLocations);
    }

    private async Task<HashSet<int>> ExpandLocationIdsAsync(int[] ids, CancellationToken cancellationToken)
    {
        var cacheKey = $"locations:tree:v1:{db.Database.GetDbConnection().Database}";
        var allLocations = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            return await db.Locations
                .AsNoTracking()
                .Select(l => new LocationNode { Id = l.Id, ParentId = l.ParentId })
                .ToListAsync(cancellationToken);
        }) ?? [];

        var childrenCacheKey = $"locations:children:v1:{db.Database.GetDbConnection().Database}";
        var children = await cache.GetOrCreateAsync(childrenCacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            var map = allLocations
                .Where(l => l.ParentId.HasValue)
                .GroupBy(l => l.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());

            return Task.FromResult(map);
        }) ?? new Dictionary<int, int[]>();

        var expanded = ids.ToHashSet();
        var frontier = new Queue<int>(ids);

        while (frontier.Count > 0)
        {
            if (!children.TryGetValue(frontier.Dequeue(), out var next))
                continue;

            foreach (var childId in next.Where(expanded.Add))
                frontier.Enqueue(childId);
        }

        return expanded;
    }

    private sealed class LocationNode
    {
        public int Id { get; init; }
        public int? ParentId { get; init; }
    }
}
