namespace AdsPortalV2.Entities;

public class UserReview
{
    public int Id { get; set; }
    public int ReviewerId { get; set; }
    public int TargetUserId { get; set; }
    public int Rating { get; set; }
    public string? Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User Reviewer { get; set; } = null!;
    public User TargetUser { get; set; } = null!;
}
