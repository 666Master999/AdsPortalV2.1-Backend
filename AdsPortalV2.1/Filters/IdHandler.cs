using AdsPortalV2.Entities;
using System.Text.Json;

namespace AdsPortalV2.Filters;

public class IdHandler : IFilterHandler
{
    public string[] Types => ["id"];

    public Task<IQueryable<Ad>> ApplyAsync(IQueryable<Ad> query, string type, IEnumerable<JsonElement> values)
    {
        var ids = values.Select(v => v.TryGetInt32(out var id) ? (int?)id : null).OfType<int>().ToList();
        if (ids.Count > 0)
            query = query.Where(ad => ids.Contains(ad.Id));
        return Task.FromResult(query);
    }
}
