using AdsPortalV2.Entities;
using System.Text.Json;

namespace AdsPortalV2.Filters;

public interface IFilterHandler
{
    string[] Types { get; }
    Task<IQueryable<Ad>> ApplyAsync(IQueryable<Ad> query, string type, IEnumerable<JsonElement> values);
}
