namespace AdsPortalV2.Models;

public class BlockDto
{
    public int TargetUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class BlockListItemDto
{
    public int TargetUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public BlockUserDto User { get; set; } = null!;
}

public class BlockUserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = default!;
    public string? AvatarUrl { get; set; }
}
