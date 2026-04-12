using AdsPortalV2.Entities;

namespace AdsPortalV2.Models;

public class AdListItemDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal? Price { get; set; }
    public bool IsNegotiable { get; set; }
    public int? CategoryId { get; set; }
    public int LocationId { get; set; }
    public LocationRef? Location { get; set; }
    // Type is kept as string to remain flexible for ad-specific classification.
    // Location.Type is a strict enum and serves as the authoritative location type.
    public string? ListingType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int UserId { get; set; }
    public int ViewsCount { get; set; }
    public int FavoritesCount { get; set; }
    public string? MainImagePath { get; set; }
    public bool IsFavorite { get; set; }
    public AdStatus? ModerationStatus { get; set; }
}
