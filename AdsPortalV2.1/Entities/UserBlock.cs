namespace AdsPortalV2.Entities;

public class UserBlock
{
    public int Id { get; set; }
    public int SourceUserId { get; set; }
    public int TargetUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User SourceUser { get; set; } = null!;
    public User TargetUser { get; set; } = null!;
}
