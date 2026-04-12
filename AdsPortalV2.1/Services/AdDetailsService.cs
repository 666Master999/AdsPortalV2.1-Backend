using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public sealed class AdDetailsService(AppDbContext db, PermissionService permissions, AdQueryService adQuery) : IAdDetailsService
{
    public async Task<AdDetailsDto?> GetAsync(int adId, int? viewerUserId, bool showModerationStatus = false, CancellationToken cancellationToken = default)
    {
        var ad = await db.Ads.AsNoTracking()
            .Where(a => a.Id == adId)
            .Select(a => new
            {
                a.Id,
                a.UserId,
                a.CategoryId,
                a.Title,
                a.Description,
                a.Price,
                a.IsNegotiable,
                a.LocationId,
                Location = a.Location == null ? null : new LocationRef(a.Location.Type, a.Location.Id, a.Location.Name),
                a.ListingType,
                a.MainImageId,
                a.CreatedAt,
                a.UpdatedAt,
                a.Status,
                a.RejectionReason,
                a.DeletedAt,
                Category = a.Category == null ? null : new AdCategoryDto(a.Category.Id, a.Category.Name, a.Category.ParentId),
                User = a.User == null ? null : new AdOwnerDto(
                    a.User.Id,
                    a.User.UserLogin,
                    a.User.UserName,
                    a.User.UserEmail,
                    a.User.UserPhoneNumber,
                    FilePathHelpers.EnsurePublicPath(a.User.AvatarPath),
                    Array.Empty<string>(),
                    a.User.CreatedAt,
                    a.User.LastActivityAt)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (ad is null)
            return null;

        var canSeeStatus = showModerationStatus;
        if (!canSeeStatus && viewerUserId.HasValue)
        {
            var ctx = await permissions.GetUserContextAsync(viewerUserId.Value, cancellationToken);
            canSeeStatus = ad.UserId == viewerUserId.Value || ctx.Permissions.Contains("ads.view_hidden");
        }

        var favoriteIds = await adQuery.GetFavoriteIdsAsync(viewerUserId, [ad.Id], cancellationToken);
        var images = await db.AdImages.AsNoTracking()
            .Where(img => img.AdId == ad.Id)
            .OrderBy(img => img.SortOrder)
            .Select(img => new AdImageDto(img.Id, img.AdId, FilePathHelpers.EnsurePublicPath(img.FilePath), img.SortOrder, img.Id == ad.MainImageId))
            .ToListAsync(cancellationToken);

        var dto = new AdDetailsDto(
            ad.Id,
            ad.UserId,
            ad.CategoryId,
            ad.Title,
            ad.Description,
            ad.Price,
            ad.IsNegotiable,
            ad.LocationId,
            ad.Location,
            ad.ListingType,
            ad.MainImageId,
            ad.CreatedAt,
            ad.UpdatedAt,
            canSeeStatus ? ad.Status : null,
            canSeeStatus ? ad.RejectionReason : null,
            ad.DeletedAt,
            ad.Category,
            ad.User,
            images,
            favoriteIds.Contains(ad.Id));

        if (dto.User is null)
            return dto;

        var roleNames = await db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == dto.UserId)
            .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, role => role.Id, (_, role) => role.Name)
            .ToListAsync(cancellationToken);

        return dto with { User = dto.User with { Roles = roleNames } };
    }
}
