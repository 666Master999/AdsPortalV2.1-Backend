using System.ComponentModel.DataAnnotations;

namespace AdsPortalV2.Entities;

public class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int UserId { get; set; }

    [MaxLength(64)]
    public string RefreshTokenHash { get; set; } = "";

    [MaxLength(200)]
    public string? DeviceName { get; set; }

    [MaxLength(50)]
    public string? IpAddress { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }

    public User User { get; set; } = null!;
}
