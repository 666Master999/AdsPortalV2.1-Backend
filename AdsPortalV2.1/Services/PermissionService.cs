using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AdsPortalV2.Services;

public sealed record UserContext(
    int UserId,
    HashSet<string> Roles,
    HashSet<string> Permissions,
    List<UserRestriction> Restrictions);

public class PermissionService(AppDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public async Task<UserContext> GetUserContextAsync(int userId)
    {
        var key = $"uctx:{userId}";
        if (cache.TryGetValue<UserContext>(key, out var cached) && cached != null)
            return cached;

        var roles = await db.Set<UserRole>()
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role.Name)
            .ToHashSetAsync();

        var permissions = roles.Count > 0
            ? await db.Set<RolePermission>()
                .AsNoTracking()
                .Where(rp => roles.Contains(rp.Role.Name))
                .Select(rp => rp.Permission.Name)
                .ToHashSetAsync()
            : [];

        var now = DateTime.UtcNow;
        var restrictions = await db.Set<UserRestriction>()
            .AsNoTracking()
            .Where(r => r.UserId == userId && (r.ExpiresAt == null || r.ExpiresAt > now))
            .ToListAsync();

        var ctx = new UserContext(userId, roles, permissions, restrictions);
        cache.Set(key, ctx, CacheTtl);
        return ctx;
    }

    public async Task<bool> HasPermissionAsync(int userId, string permission)
    {
        var ctx = await GetUserContextAsync(userId);
        return ctx.Permissions.Contains(permission);
    }

    public async Task<bool> HasRoleAsync(int userId, string role)
    {
        var ctx = await GetUserContextAsync(userId);
        return ctx.Roles.Contains(role);
    }

    public async Task<bool> HasActiveRestrictionAsync(int userId, RestrictionType type)
    {
        var ctx = await GetUserContextAsync(userId);
        return ctx.Restrictions.Any(r => r.Type == type);
    }

    public void InvalidateCache(int userId) => cache.Remove($"uctx:{userId}");
}
