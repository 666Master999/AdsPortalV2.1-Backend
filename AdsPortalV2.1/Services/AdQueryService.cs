using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AdsPortalV2.Services;

public class AdQueryService(AppDbContext db, IMemoryCache cache)
{
    public async Task<IQueryable<Ad>> BuildQueryAsync(
        IQueryable<Ad> query,
        AdsQuery q,
        int[] locationIds,
        int[] categoryIds,
        AdStatus? status)
    {
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var search = $"%{q.Search.Trim()}%";
            query = query.Where(ad =>
                EF.Functions.Like(ad.Title, search) ||
                ad.Description != null && EF.Functions.Like(ad.Description, search));
        }

        if (locationIds.Length > 0)
        {
            var expandedLocations = await ExpandLocationIdsAsync(locationIds);
            query = query.Where(ad => expandedLocations.Contains(ad.LocationId));
        }

        if (categoryIds.Length > 0)
            query = query.Where(ad => ad.CategoryId.HasValue && categoryIds.Contains(ad.CategoryId.Value));

        if (!string.IsNullOrWhiteSpace(q.Type))
            query = query.Where(ad => ad.ListingType == q.Type);

        if (q.PriceFrom.HasValue)
            query = query.Where(ad => ad.Price >= q.PriceFrom.Value);

        if (q.PriceTo.HasValue)
            query = query.Where(ad => ad.Price <= q.PriceTo.Value);

        if (q.DateFrom.HasValue)
            query = query.Where(ad => ad.CreatedAt >= q.DateFrom.Value.ToDateTime(TimeOnly.MinValue));

        if (q.DateTo.HasValue)
            query = query.Where(ad => ad.CreatedAt < q.DateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));

        if (q.UserId.HasValue)
            query = query.Where(ad => ad.UserId == q.UserId.Value);

        if (status.HasValue)
            query = query.Where(ad => ad.Status == status.Value);

        return query;
    }

    public async Task<HashSet<int>> GetFavoriteIdsAsync(int? currentUserId, IEnumerable<int> adIds)
    {
        if (!currentUserId.HasValue)
            return [];

        var ids = adIds.Distinct().ToArray();
        if (ids.Length == 0)
            return [];

        return await db.UserFavoriteAds
            .AsNoTracking()
            .Where(f => f.UserId == currentUserId.Value && ids.Contains(f.AdId))
            .Select(f => f.AdId)
            .ToHashSetAsync();
    }

    private async Task<HashSet<int>> ExpandLocationIdsAsync(int[] ids)
    {
        var allLocations = await cache.GetOrCreateAsync("ads:locations", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await db.Locations
                .AsNoTracking()
                .Select(l => new { l.Id, l.ParentId })
                .ToListAsync();
        }) ?? [];

        var children = allLocations
            .Where(l => l.ParentId.HasValue)
            .GroupBy(l => l.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());

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
}
