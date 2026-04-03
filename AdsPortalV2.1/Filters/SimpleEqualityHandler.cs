using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AdsPortalV2.Filters;

public class SimpleEqualityHandler : IFilterHandler
{
    public string[] Types => ["city", "userid", "status"];

    public Task<IQueryable<Ad>> ApplyAsync(IQueryable<Ad> query, string type, IEnumerable<JsonElement> values)
    {
        var v = values.FirstOrDefault();

        switch (type)
        {
            case "city" when v.TryGetInt32(out var cityId):
                query = query.Where(ad => ad.CityId == cityId);
                break;
            case "city" when v.ValueKind == JsonValueKind.String:
                query = query.Where(ad => ad.CityRef != null && EF.Functions.Like(ad.CityRef.Name, v.GetString()!));
                break;
            case "userid" when v.TryGetInt32(out var userId):
                query = query.Where(ad => ad.UserId == userId);
                break;
            case "status" when v.ValueKind == JsonValueKind.String && Enum.TryParse<ModerationStatus>(v.GetString(), true, out var status):
                query = query.Where(ad => ad.ModerationStatus == status);
                break;
        }

        return Task.FromResult(query);
    }
}
