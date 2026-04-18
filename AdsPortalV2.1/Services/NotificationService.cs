using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AdsPortalV2.Services;

public class NotificationService(AppDbContext db, IHubContext<SystemNotificationHub> hub, ILogger<NotificationService> logger) : INotificationService
{
    public async Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        // Diagnostic: log DbContext instance and notification info
        try { logger.LogWarning("NOTIFICATION DB CONTEXT ID (NotificationService): {Id}", db.ContextId.InstanceId); } catch {}
        try { logger.LogWarning("Saving notification for UserId={UserId} AdId={AdId} OutboxId={OutboxId}", notification.UserId, notification.AdId, notification.OutboxMessageId); } catch {}

        // preserve OutboxMessageId if provided in Notification.OutboxMessageId (set by handler)
        db.Notifications.Add(notification);

        try
        {
            // diagnostic: how many tracked OutboxMessage/Notification entries
            try { var tracked = db.ChangeTracker.Entries<Notification>().Count(); logger.LogWarning("TRACKED NOTIFICATION ENTRIES BEFORE SAVE: {Count}", tracked); } catch {}

            await db.SaveChangesAsync(cancellationToken);
            try { logger.LogWarning("NOTIFICATION SAVED: Id={Id}", notification.Id); } catch {}
        }
        catch (Exception ex)
        {
            try { logger.LogError(ex, "Failed to save notification for UserId={UserId} AdId={AdId}", notification.UserId, notification.AdId); } catch {}
            throw;
        }

        string? image = null;
        string? adTitle = null;
        if (notification.AdId.HasValue)
        {
            var ad = await db.Ads
                .AsNoTracking()
                .Where(a => a.Id == notification.AdId.Value)
                .Select(a => new { a.MainImageId, a.Title })
                .FirstOrDefaultAsync(cancellationToken);

            if (ad != null)
            {
                adTitle = ad.Title;
                if (ad.MainImageId.HasValue)
                {
                    image = await db.AdImages
                        .AsNoTracking()
                        .Where(i => i.Id == ad.MainImageId.Value)
                        .Select(i => i.FilePath)
                        .FirstOrDefaultAsync(cancellationToken);
                }
            }
        }

        var dto = NotificationMapper.ToDto(notification, image, adTitle);
        await hub.Clients.Group($"user:{notification.UserId}").SendAsync("notificationCreated", dto, cancellationToken);
    }
}
