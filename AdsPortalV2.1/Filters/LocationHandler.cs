using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace AdsPortalV2.Filters;

public class LocationHandler(AppDbContext db, IMemoryCache cache) : IFilterHandler
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public string[] Types => ["location"];

    public async Task<IQueryable<Ad>> ApplyAsync(IQueryable<Ad> query, string type, IEnumerable<JsonElement> values)
    {
        var refs = values
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!.Split(':'))
            .Where(p => p.Length == 2 && p[0] is "city" or "region" or "district" && int.TryParse(p[1], out _))
            .Select(p => new LocationRef(p[0], int.Parse(p[1])))
            .ToList();

        if (refs.Count == 0) return query;

        var (regionIds, cityIds, districtIds) = await NormalizeAsync(refs);

        if (regionIds.Count > 0 || cityIds.Count > 0 || districtIds.Count > 0)
            query = query.Where(ad =>
                (regionIds.Count > 0 && ad.CityRef != null && regionIds.Contains(ad.CityRef.RegionId)) ||
                (cityIds.Count > 0 && ad.CityId != null && cityIds.Contains(ad.CityId.Value)) ||
                (districtIds.Count > 0 && ad.DistrictId != null && districtIds.Contains(ad.DistrictId.Value)));

        return query;
    }

    private async Task<(List<int> regionIds, List<int> cityIds, List<int> districtIds)> NormalizeAsync(List<LocationRef> refs)
    {
        var normalized = refs.OrderBy(x => x.Type).ThenBy(x => x.Id).ToArray();
        var cacheKey = $"locations:normalize:{string.Join(',', normalized.DistinctBy(x => (x.Type, x.Id)).Select(x => $"{x.Type}:{x.Id}"))}";

        if (cache.TryGetValue(cacheKey, out (int[] regionIds, int[] cityIds, int[] districtIds) cached))
            return (cached.regionIds.ToList(), cached.cityIds.ToList(), cached.districtIds.ToList());

        var regionIds   = normalized.Where(x => x.Type == "region").Select(x => x.Id).ToHashSet();
        var cityIds     = normalized.Where(x => x.Type == "city").Select(x => x.Id).ToHashSet();
        var districtIds = normalized.Where(x => x.Type == "district").Select(x => x.Id).ToHashSet();

        if (cityIds.Count > 0 && regionIds.Count > 0)
        {
            var cityRegions = await db.Cities.Where(c => cityIds.Contains(c.Id)).Select(c => new { c.Id, c.RegionId }).ToListAsync();
            foreach (var c in cityRegions)
                if (regionIds.Contains(c.RegionId)) regionIds.Remove(c.RegionId);
        }

        if (districtIds.Count > 0 && (regionIds.Count > 0 || cityIds.Count > 0))
        {
            var districtInfo = await db.Districts.Where(d => districtIds.Contains(d.Id)).Select(d => new { d.Id, d.CityId, RegionId = d.City!.RegionId }).ToListAsync();
            foreach (var d in districtInfo)
            {
                if (cityIds.Contains(d.CityId)) cityIds.Remove(d.CityId);
                if (regionIds.Contains(d.RegionId)) regionIds.Remove(d.RegionId);
            }
        }

        var result = (regionIds.ToArray(), cityIds.ToArray(), districtIds.ToArray());
        cache.Set(cacheKey, result, CacheTtl);
        return (result.Item1.ToList(), result.Item2.ToList(), result.Item3.ToList());
    }
}
