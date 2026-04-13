using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public class NotificationService(AppDbContext db, IHubContext<SystemNotificationHub> hub) : INotificationService
{
    public async Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken);

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
