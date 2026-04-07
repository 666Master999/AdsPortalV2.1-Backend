using System.Text.Json.Serialization;

namespace AdsPortalV2.Models;

public sealed class ApiError
{
    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("fields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Fields { get; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; }

    public ApiError(string code, string message)
    {
        Code = code;
        Message = message;
    }

    public ApiError(string code, string message, IReadOnlyDictionary<string, string> fields)
        : this(code, message)
    {
        Fields = fields;
    }

    public ApiError(string code, string message, object details)
        : this(code, message)
    {
        Details = details;
    }
}
