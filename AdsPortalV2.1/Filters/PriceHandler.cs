using AdsPortalV2.Entities;
using System.Text.Json;

namespace AdsPortalV2.Filters;

public class PriceHandler : IFilterHandler
{
    public string[] Types => ["pricefrom", "priceto"];

    public Task<IQueryable<Ad>> ApplyAsync(IQueryable<Ad> query, string type, IEnumerable<JsonElement> values)
    {
        var v = values.FirstOrDefault();
        if (!v.TryGetDecimal(out var price)) return Task.FromResult(query);

        query = type == "pricefrom" 
            ? query.Where(ad => ad.Price >= price) 
            : query.Where(ad => ad.Price <= price);

        return Task.FromResult(query);
    }
}
