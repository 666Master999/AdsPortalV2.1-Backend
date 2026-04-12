using System.Text.Json.Serialization;

namespace AdsPortalV2.Models;

public sealed class ApiError
{
    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; }

    [JsonPropertyName("issues")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyCollection<PatchIssueDto>? Issues { get; }

    public ApiError(string code, string message)
    {
        Code = code;
        Message = message;
        Issues = [new PatchIssueDto(code, null, message)];
    }
    public ApiError(string code, string message, IReadOnlyCollection<PatchIssueDto> issues)
    {
        Code = code;
        Message = message;
        Issues = issues;
    }

    public ApiError(string code, string message, object details)
        : this(code, message)
    {
        Details = details;
    }
}
