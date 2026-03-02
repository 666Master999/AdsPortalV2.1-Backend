namespace AdsPortalV2.Entities;

public class Complaint
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int AdId { get; set; }
    public string Reason { get; set; } = "";
    public bool IsResolved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public Ad Ad { get; set; } = null!;
}
