using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public class AdVisibilityService(AppDbContext db, PermissionService permissions)
{
    public Task<bool> CanViewAdAsync(int? viewerUserId, Ad ad)
        => CanViewAdAsync(viewerUserId, ad.UserId, ad.Status);

    public async Task<bool> CanViewAdAsync(int? viewerUserId, int adUserId, AdStatus adStatus)
    {
        if (!viewerUserId.HasValue)
            return adStatus == AdStatus.Active;

        var userId = viewerUserId.Value;
        var ctx = await permissions.GetUserContextAsync(userId);

        if (ctx.Restrictions.Any(r => r.Type == RestrictionType.LoginBan))
            return false;

        if (adUserId == userId)
            return true;

        var isBlocked = await db.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.SourceUserId == userId && b.TargetUserId == adUserId) ||
            (b.SourceUserId == adUserId && b.TargetUserId == userId));
        if (isBlocked)
            return false;

        if (ctx.Permissions.Contains("ads.view_hidden"))
            return true;

        var authorBanned = await db.UserRestrictions.AsNoTracking().AnyAsync(r =>
            r.UserId == adUserId &&
            r.Type == RestrictionType.LoginBan &&
            (r.ExpiresAt == null || r.ExpiresAt > DateTime.UtcNow));

        if (authorBanned)
            return false;

        return adStatus == AdStatus.Active;
    }

    public async Task<IQueryable<Ad>> ApplyVisibilityAsync(IQueryable<Ad> query, int? viewerUserId)
    {
        if (!viewerUserId.HasValue)
            return query.Where(a => a.Status == AdStatus.Active);

        var userId = viewerUserId.Value;
        var ctx = await permissions.GetUserContextAsync(userId);
        var canViewHidden = ctx.Permissions.Contains("ads.view_hidden");

        var blockedUserIds = await db.UserBlocks.AsNoTracking()
            .Where(b => b.SourceUserId == userId || b.TargetUserId == userId)
            .Select(b => b.SourceUserId == userId ? b.TargetUserId : b.SourceUserId)
            .Distinct()
            .ToArrayAsync();

        var bannedUserIds = await db.UserRestrictions.AsNoTracking()
            .Where(r => r.Type == RestrictionType.LoginBan && (r.ExpiresAt == null || r.ExpiresAt > DateTime.UtcNow))
            .Select(r => r.UserId)
            .Distinct()
            .ToArrayAsync();

        return query.Where(a =>
            !blockedUserIds.Contains(a.UserId) &&
            !bannedUserIds.Contains(a.UserId) &&
            (a.UserId == userId || canViewHidden || a.Status == AdStatus.Active));
    }
}
