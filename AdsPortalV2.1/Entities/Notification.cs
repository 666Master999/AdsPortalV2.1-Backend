namespace AdsPortalV2.Entities;

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? AdId { get; set; }
    public NotificationType Type { get; set; }
    public string? Reason { get; set; }
    public string? PreviewJson { get; set; }
    public string? DataJson { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public Ad? Ad { get; set; }
}
