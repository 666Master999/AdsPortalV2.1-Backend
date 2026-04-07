using System;

namespace AdsPortalV2.Entities
{
    public class UserFavoriteAd
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int AdId { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        // Навигационные свойства
        public User User { get; set; } = null!;
        public Ad Ad { get; set; } = null!;
    }
}