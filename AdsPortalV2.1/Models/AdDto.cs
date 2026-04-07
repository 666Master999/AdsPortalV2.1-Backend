using AdsPortalV2.Entities;

namespace AdsPortalV2.Models;

public sealed record AdDto(
    int Id,
    int UserId,
    int? CategoryId,
    string Title,
    string? Description,
    decimal? Price,
    string? ListingType,
    bool IsNegotiable,
    int LocationId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    AdStatus Status,
    string? RejectionReason,
    DateTime? DeletedAt,
    int ViewsCount,
    int FavoritesCount);
