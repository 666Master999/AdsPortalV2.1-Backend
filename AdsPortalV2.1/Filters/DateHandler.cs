using AdsPortalV2.Entities;
using System.Text.Json;

namespace AdsPortalV2.Filters;

public class DateHandler : IFilterHandler
{
    public string[] Types => ["datefrom", "dateto"];

    public Task<IQueryable<Ad>> ApplyAsync(IQueryable<Ad> query, string type, IEnumerable<JsonElement> values)
    {
        var v = values.FirstOrDefault();
        if (v.ValueKind != JsonValueKind.String || !DateOnly.TryParse(v.GetString(), out var date))
            return Task.FromResult(query);

        query = type == "datefrom"
            ? query.Where(ad => ad.CreatedAt >= date.ToDateTime(TimeOnly.MinValue))
            : query.Where(ad => ad.CreatedAt < date.AddDays(1).ToDateTime(TimeOnly.MinValue));

        return Task.FromResult(query);
    }
}
