using AdsPortalV2.Entities;

namespace AdsPortalV2.Services;

public interface INotificationFactory
{
    Notification CreateAdRejected(int userId, int adId, string title, string reason, string actorName);
    Notification CreateAdApproved(int userId, int adId, string title, string actorName);
}
