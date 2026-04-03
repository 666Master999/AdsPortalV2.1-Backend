using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdsPortalV2.Models;

public record FilterRef(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] JsonElement Value);
