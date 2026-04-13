using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using System.Text.Json;

namespace AdsPortalV2.Services;

public static class NotificationMapper
{
    public static NotificationDto ToDto(Notification notification, string? image, string? adTitle) => new(
        notification.Id,
        notification.Type.ToString(),
        notification.IsRead,
        notification.CreatedAt,
        notification.AdId,
        adTitle,
        notification.Reason,
        MergeMainImagePath(notification.PreviewJson, image),
        MergeActorName(notification.DataJson));

    public static NotificationDto ToDto(Notification notification)
        => ToDto(notification, null, null);

    private static string? MergeMainImagePath(string? previewJson, string? image)
    {
        var preview = DeserializePreview(previewJson);
        if (!string.IsNullOrEmpty(image)) return image;
        return preview?.MainImagePath;
    }

    private static string? MergeActorName(string? dataJson)
    {
        var data = Deserialize(dataJson);
        return data?.ActorName;
    }

    private static NotificationPreviewDto? DeserializePreview(string? json)
        => json == null ? null : JsonSerializer.Deserialize<NotificationPreviewDto>(json);

    private static NotificationDataDto? Deserialize(string? json)
        => json == null ? null : JsonSerializer.Deserialize<NotificationDataDto>(json);
}
