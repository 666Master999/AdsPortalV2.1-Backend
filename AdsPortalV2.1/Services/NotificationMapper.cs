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

    private static NotificationPreviewDto? MergePreview(string? json, string? image)
    {
        var preview = DeserializePreview(json);
        if (preview == null && image == null)
            return null;

        return preview is null
            ? new NotificationPreviewDto(string.Empty, image)
            : preview with { MainImagePath = image ?? preview.MainImagePath };
    }

    private static NotificationPreviewDto? DeserializePreview(string? json)
        => json == null ? null : JsonSerializer.Deserialize<NotificationPreviewDto>(json);

    private static NotificationDataDto? Deserialize(string? json)
        => json == null ? null : JsonSerializer.Deserialize<NotificationDataDto>(json);
}
