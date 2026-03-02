using System.Text.Json.Serialization;

namespace AdsPortalV2.Entities;

public class User
{
    public int Id { get; set; }
    public string UserLogin { get; set; } = string.Empty; 
    [JsonIgnore]
    public string UserPasswordHash { get; set; } = string.Empty;
    public string? UserName { get; set; } 
    public string? UserEmail { get; set; }
    public string? UserPhoneNumber { get; set; } = "";
    public string? AvatarPath { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsBlocked { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Ad> Ads { get; set; } = [];
    public List<UserSession> Sessions { get; set; } = [];
    public List<UserBlock> Blocks { get; set; } = [];
    public List<UserReview> ReviewsReceived { get; set; } = [];
    public List<UserReview> ReviewsWritten { get; set; } = [];
    public List<Favorite> Favorites { get; set; } = [];
    public List<ChatMessage> ChatMessages { get; set; } = [];
    public List<Complaint> Complaints { get; set; } = [];
    public List<AdminLog> AdminLogs { get; set; } = [];
}
