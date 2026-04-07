namespace AdsPortalV2.Entities;

public class UserRestriction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public RestrictionType Type { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public User User { get; set; } = null!;
}
