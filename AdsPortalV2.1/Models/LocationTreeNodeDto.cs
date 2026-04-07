using System.Text.Json.Serialization;

namespace AdsPortalV2.Models;

public sealed class LocationTreeNodeDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public AdsPortalV2.Entities.LocationType Type { get; set; }

    [JsonPropertyName("children")]
    public List<LocationTreeNodeDto> Children { get; set; } = [];
}
