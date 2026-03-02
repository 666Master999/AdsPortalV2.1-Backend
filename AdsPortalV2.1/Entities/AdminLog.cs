namespace AdsPortalV2.Entities;

public class AdminLog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
