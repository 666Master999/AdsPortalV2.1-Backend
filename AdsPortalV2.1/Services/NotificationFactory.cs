using AdsPortalV2.Entities;
using System.Text.Json;

namespace AdsPortalV2.Services;

public class NotificationFactory : INotificationFactory
{
    public Notification CreateAdRejected(int userId, int adId, string title, string reason, string actorName)
    {
        var preview = new { title };
        var data = new { actorName, reason };

        return new Notification
        {
            UserId = userId,
            AdId = adId,
            Type = NotificationType.AdRejected,
            Reason = reason,
            PreviewJson = JsonSerializer.Serialize(preview),
            DataJson = JsonSerializer.Serialize(data),
            CreatedAt = DateTime.UtcNow
        };
    }

    public Notification CreateAdApproved(int userId, int adId, string title, string actorName)
    {
        var preview = new { title };
        var data = new { actorName };

        return new Notification
        {
            UserId = userId,
            AdId = adId,
            Type = NotificationType.AdApproved,
            Reason = null,
            PreviewJson = JsonSerializer.Serialize(preview),
            DataJson = JsonSerializer.Serialize(data),
            CreatedAt = DateTime.UtcNow
        };
    }
}
