namespace AdsPortalV2.Entities;

public enum ModerationStatus
{
    Pending,    // На модерации
    Approved,   // Одобрено
    Rejected,   // Отклонено
    Hidden      // Скрыто (например, по жалобе)
}
