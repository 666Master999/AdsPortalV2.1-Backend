using AdsPortalV2.Entities;
using System.Text.Json;

namespace AdsPortalV2.Filters;

public class SearchHandler : IFilterHandler
{
    public string[] Types => ["search"];

    public Task<IQueryable<Ad>> ApplyAsync(IQueryable<Ad> query, string type, IEnumerable<JsonElement> values)
    {
        var v = values.FirstOrDefault();
        if (v.ValueKind == JsonValueKind.String)
            query = query.Where(ad => ad.Title.Contains(v.GetString()!));
        return Task.FromResult(query);
    }
}
