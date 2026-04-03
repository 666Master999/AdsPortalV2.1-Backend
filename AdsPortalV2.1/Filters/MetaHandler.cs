using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AdsPortalV2.Filters;

public class MetaHandler : IFilterHandler
{
    public string[] Types => ["category", "type"];

    public Task<IQueryable<Ad>> ApplyAsync(IQueryable<Ad> query, string type, IEnumerable<JsonElement> values)
    {
        var v = values.FirstOrDefault();

        if (type == "category" && v.TryGetInt32(out var catId))
            query = query.Where(ad => ad.CategoryId == catId);
        else if (type == "type" && v.ValueKind == JsonValueKind.String)
            query = query.Where(ad => EF.Functions.Like(ad.Type, v.GetString()!));

        return Task.FromResult(query);
    }
}
