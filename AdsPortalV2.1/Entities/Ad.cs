using System.ComponentModel.DataAnnotations;

namespace AdsPortalV2.Entities;

public class Ad
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? CategoryId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public decimal? Price { get; set; }

    public string? City { get; set; }
    public string? Type { get; set; }
    public bool IsNegotiable { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }
    public ModerationStatus ModerationStatus { get; set; }

    public int ViewsCount { get; set; }
    public int FavoritesCount { get; set; }

    public Category? Category { get; set; }
    public User? User { get; set; }
    public ICollection<AdImage> Images { get; set; } = new List<AdImage>();
    public ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
}