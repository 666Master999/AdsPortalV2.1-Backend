using System.Text.Json;
using System.Text.Json.Serialization;
using AdsPortalV2.Models;

namespace AdsPortalV2.Services;

public static class DialogHelpers
{
    public static readonly JsonSerializerOptions s_jsonl = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int FindFileIndex(DialogMeta meta, int messageId)
    {
        var ids = meta.FileFirstMessageIds;
        if (ids.Count == 0) return 0;
        int lo = 0, hi = ids.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ids[mid] <= messageId) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public static string BuildFileName(int index) => $"messages_{index}.jsonl";
}
