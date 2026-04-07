using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public class NotificationService(AppDbContext db, IHubContext<NotificationHub> hub) : INotificationService
{
    public async Task SendAsync(Notification notification)
    {
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        string? image = null;
        if (notification.AdId.HasValue)
        {
            var mainImageId = await db.Ads
                .AsNoTracking()
                .Where(a => a.Id == notification.AdId.Value)
                .Select(a => a.MainImageId)
                .FirstOrDefaultAsync();

            if (mainImageId.HasValue)
            {
                image = await db.AdImages
                    .AsNoTracking()
                    .Where(i => i.Id == mainImageId.Value)
                    .Select(i => i.FilePath)
                    .FirstOrDefaultAsync();
            }
        }

        var dto = NotificationMapper.ToDto(notification, image);
        await hub.Clients.Group($"user:{notification.UserId}").SendAsync("notificationCreated", dto);
    }
}
