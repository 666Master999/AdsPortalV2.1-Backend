namespace AdsPortalV2.Entities;

public class Favorite
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int AdId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public Ad Ad { get; set; } = null!;
}
