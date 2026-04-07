using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AdsPortalV2.Entities;

public class User
{
    public int Id { get; set; }
    [MaxLength(50)]
    public string UserLogin { get; set; } = string.Empty;
    [JsonIgnore]
    public string UserPasswordHash { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? UserName { get; set; }
    [MaxLength(200)]
    public string? UserEmail { get; set; }
    [MaxLength(30)]
    public string? UserPhoneNumber { get; set; }
    public string? AvatarPath { get; set; }
    public int TokenVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    public List<Ad> Ads { get; set; } = [];
    public List<UserSession> Sessions { get; set; } = [];
    public List<UserRole> UserRoles { get; set; } = [];
    public List<UserRestriction> Restrictions { get; set; } = [];
    public List<UserBlock> BlocksGiven { get; set; } = [];
    public List<UserBlock> BlocksReceived { get; set; } = [];
    public List<UserReview> ReviewsReceived { get; set; } = [];
    public List<UserReview> ReviewsWritten { get; set; } = [];
    public List<AuditLog> AuditLogs { get; set; } = [];
    public List<Conversation> ConversationsAsSeller { get; set; } = [];
    public List<Conversation> ConversationsAsBuyer { get; set; } = [];
    public List<UserFavoriteAd> Favorites { get; set; } = [];
}
