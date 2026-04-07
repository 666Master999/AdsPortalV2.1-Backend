using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using System.Text.Json;

namespace AdsPortalV2.Services;

public static class NotificationMapper
{
    public static NotificationDto ToDto(Notification notification, string? image) => new(
        notification.Id,
        notification.Type.ToString(),
        notification.IsRead,
        notification.CreatedAt,
        notification.Reason,
        MergePreview(notification.PreviewJson, image),
        Deserialize(notification.DataJson));

    public static NotificationDto ToDto(Notification notification)
        => ToDto(notification, null);

    private static Dictionary<string, object?>? MergePreview(string? json, string? image)
    {
        var preview = DeserializeDictionary(json);
        if (preview == null && image == null)
            return null;

        preview ??= [];
        preview["image"] = image;
        return preview;
    }

    private static Dictionary<string, object?>? DeserializeDictionary(string? json)
        => json == null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(json);

    private static object? Deserialize(string? json)
        => json == null ? null : JsonSerializer.Deserialize<object>(json);
}
