using AdsPortalV2.Entities;

namespace AdsPortalV2.Services;

public interface INotificationService
{
    Task SendAsync(Notification notification);
}
