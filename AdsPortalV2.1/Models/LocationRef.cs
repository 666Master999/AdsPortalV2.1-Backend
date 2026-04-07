using System.Text.Json.Serialization;

namespace AdsPortalV2.Models;

public record LocationRef(
    [property: JsonPropertyName("type")] AdsPortalV2.Entities.LocationType Type,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name = null);
