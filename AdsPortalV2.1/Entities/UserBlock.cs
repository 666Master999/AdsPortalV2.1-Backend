namespace AdsPortalV2.Entities;

public class UserBlock
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Reason { get; set; } = "";
    public DateTime BlockedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UnblockedAt { get; set; }

    public User User { get; set; } = null!;
}
