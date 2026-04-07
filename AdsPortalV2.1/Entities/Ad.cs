using System.ComponentModel.DataAnnotations;

namespace AdsPortalV2.Entities;

public class Ad
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? CategoryId { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    [MaxLength(5000)]
    public string? Description { get; set; }

    public decimal? Price { get; set; }

    [MaxLength(50)]
    public string? ListingType { get; set; }
    public bool IsNegotiable { get; set; }

    public int LocationId { get; set; }
    public Location? Location { get; set; }
    public int? MainImageId { get; set; }
    public AdImage? MainImage { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public AdStatus Status { get; set; } = AdStatus.PendingModeration;
    public string? RejectionReason { get; set; }
    public DateTime? DeletedAt { get; set; }

    public int ViewsCount { get; set; }
    public int FavoritesCount { get; set; }

    public Category? Category { get; set; }
    public User? User { get; set; }
    public ICollection<AdImage> Images { get; set; } = [];
    public ICollection<Conversation> Conversations { get; set; } = [];
}