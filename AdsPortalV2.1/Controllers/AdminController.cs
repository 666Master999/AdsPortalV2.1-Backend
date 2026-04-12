using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("admin")]
[Authorize]
public class AdminController(AppDbContext db, PermissionService perms, IDomainEventPublisher events, INotificationFactory notificationFactory, INotificationService notifications) : ControllerBase
{
    [Authorize(Policy = AuthorizationPolicies.CanViewHiddenAd)]
    [HttpGet("ads")]
    public async Task<ActionResult<PagedResultDto<AdminAdListItemDto>>> GetAds([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? status = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = db.Ads.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var set = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Enum.TryParse<AdStatus>(x, true, out var parsed) ? parsed : (AdStatus?)null)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .ToHashSet();
            if (set.Count > 0)
                query = query.Where(a => set.Contains((AdStatus)a.Status));
        }

        var total = await query.CountAsync();
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling((double)total / pageSize);
        page = Math.Min(page, totalPages);
        var ads = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AdminAdListItemDto(
                a.Id,
                a.UserId,
                a.CategoryId,
                a.Title,
                a.Description,
                a.Price,
                a.ListingType,
                a.IsNegotiable,
                a.LocationId,
                a.CreatedAt,
                a.UpdatedAt,
                a.Status,
                a.RejectionReason,
                a.DeletedAt,
                a.ViewsCount,
                a.FavoritesCount,
                a.User == null ? null : new AdminAdOwnerDto(a.User.Id, a.User.UserLogin, a.User.UserName, a.User.AvatarPath),
                a.Category == null ? null : new AdCategoryDto(a.Category.Id, a.Category.Name, a.Category.ParentId),
                a.Location == null ? null : new LocationRef(a.Location.Type, a.Location.Id, a.Location.Name),
                a.Images.OrderBy(img => img.SortOrder).Select(img => img.FilePath).FirstOrDefault()))
            .ToListAsync();

        return Ok(new PagedResultDto<AdminAdListItemDto>(ads, total, page, pageSize, totalPages));
    }

    [Authorize(Policy = "CanViewLogs")]
    [HttpGet("users")]
    public async Task<ActionResult<PagedResultDto<UserDto>>> GetUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        var total = await db.Users.CountAsync();
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling((double)total / pageSize);
        page = Math.Min(page, totalPages);
        var users = await db.Users.AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UserDto(
                u.Id,
                u.UserLogin,
                u.UserName,
                u.UserEmail,
                u.UserPhoneNumber,
                u.AvatarPath,
                u.CreatedAt,
                u.LastActivityAt,
                u.UserRoles.Select(ur => ur.Role.Name).ToList()))
            .ToListAsync();

        return Ok(new PagedResultDto<UserDto>(users, total, page, pageSize, totalPages));
    }

    [Authorize(Policy = "CanViewLogs")]
    [HttpGet("logs")]
    public async Task<ActionResult<PagedResultDto<AdminAuditLogDto>>> GetLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        var total = await db.AuditLogs.CountAsync();
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling((double)total / pageSize);
        page = Math.Min(page, totalPages);
        var logs = await db.AuditLogs.AsNoTracking()
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new AdminAuditLogDto(l.Id, l.ActorUserId, l.TargetUserId, l.Action, l.TargetType, l.TargetId, l.Reason, l.OldValue, l.NewValue, l.Timestamp))
            .ToListAsync();
        return Ok(new PagedResultDto<AdminAuditLogDto>(logs, total, page, pageSize, totalPages));
    }

    [Authorize(Policy = AuthorizationPolicies.CanBanUser)]
    [HttpGet("restrictions/types")]
    public ActionResult<IReadOnlyCollection<RestrictionTypeDto>> GetRestrictionTypes() => Ok(Enum.GetValues<RestrictionType>().Select(type => new RestrictionTypeDto(type.ToString(), type.ToString())).ToArray());

    // ?? Restrictions ??

    [Authorize(Policy = AuthorizationPolicies.CanBanUser)]
    [HttpPost("users/{id:int}/restrictions")]
    public async Task<ActionResult<RestrictionActionDto>> AddRestriction(int id, [FromBody] JsonElement body)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();

        var type = GetString(body, "type");
        if (string.IsNullOrWhiteSpace(type))
            return BadRequest(new ApiError("validation_error", "Invalid restriction type."));

        if (!Enum.TryParse<RestrictionType>(type, true, out var rType))
            return BadRequest(new ApiError("validation_error", "Invalid restriction type."));

        var typeName = rType.ToString();
        var now = DateTime.UtcNow;
        var reason = GetString(body, "reason");
        var expiresAt = GetDateTime(body, "expiresAt");

        var existing = await db.UserRestrictions
            .FirstOrDefaultAsync(r => r.UserId == id && r.Type == rType && (r.ExpiresAt == null || r.ExpiresAt > now));
        if (existing != null)
            return Conflict(new ApiError("conflict", "Restriction already active."));

        db.UserRestrictions.Add(new UserRestriction
        {
            UserId = id,
            Type = rType,
            ExpiresAt = expiresAt,
            Reason = reason
        });

        if (rType == RestrictionType.LoginBan)
        {
            user.TokenVersion++;
            await db.AuthSessions
                .Where(s => s.UserId == id && !s.IsRevoked)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsRevoked, true)
                    .SetProperty(x => x.RevokedAt, now));
        }

        var expiresText = expiresAt.HasValue ? expiresAt.Value.ToString("O") : "permanent";
        var logValue = $"{typeName}; expires={expiresText}; sessionsRevoked={rType == RestrictionType.LoginBan}";

        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorId,
            TargetUserId = id,
            Action = $"restriction.add.{typeName}",
            TargetType = "User",
            TargetId = id,
            Reason = reason,
            NewValue = logValue
        });

        await db.SaveChangesAsync();
        perms.InvalidateCache(id);
        events.Publish(new UserBanned(id, actorId));

        return Ok(new RestrictionActionDto(id, typeName, reason, expiresAt, rType == RestrictionType.LoginBan));
    }

    [Authorize(Policy = "CanUnbanUser")]
    [HttpDelete("users/{id:int}/restrictions/{type}")]
    public async Task<ActionResult<RestrictionActionDto>> RemoveRestriction(int id, string type)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        if (!Enum.TryParse<RestrictionType>(type, true, out var rType))
            return BadRequest(new ApiError("validation_error", "Invalid restriction type."));

        var typeName = rType.ToString();
        var removed = await db.UserRestrictions
            .Where(r => r.UserId == id && r.Type == rType)
            .ExecuteDeleteAsync();

        if (removed == 0) return NotFound();

        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorId,
            TargetUserId = id,
            Action = $"restriction.remove.{typeName}",
            TargetType = "User",
            TargetId = id
        });

        await db.SaveChangesAsync();
        perms.InvalidateCache(id);

        return Ok(new RestrictionActionDto(id, typeName, null, null, false));
    }

    // ?? Roles ??

    [Authorize(Policy = "CanAssignRole")]
    [HttpPost("users/{id:int}/roles")]
    public async Task<ActionResult<RoleActionDto>> AssignRole(int id, [FromBody] RoleDto dto)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == dto.Role);
        if (role == null) return BadRequest(new ApiError("validation_error", "Unknown role."));

        var exists = await db.UserRoles.AnyAsync(ur => ur.UserId == id && ur.RoleId == role.Id);
        if (exists) return Conflict(new ApiError("conflict", "Role already assigned."));

        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();

        db.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id });
        user.TokenVersion++;

        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorId,
            TargetUserId = id,
            Action = "role.assign",
            TargetType = "User",
            TargetId = id,
            NewValue = dto.Role
        });

        await db.SaveChangesAsync();
        perms.InvalidateCache(id);
        events.Publish(new RoleAssigned(id, dto.Role, actorId));

        return Ok(new RoleActionDto(id, dto.Role));
    }

    [Authorize(Policy = "CanRevokeRole")]
    [HttpDelete("users/{id:int}/roles/{role}")]
    public async Task<ActionResult<RoleActionDto>> RevokeRole(int id, string role)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        var removed = await db.UserRoles
            .Where(ur => ur.UserId == id && ur.Role.Name == role)
            .ExecuteDeleteAsync();

        if (removed == 0) return NotFound();

        var user = await db.Users.FindAsync(id);
        if (user != null) user.TokenVersion++;

        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorId,
            TargetUserId = id,
            Action = "role.revoke",
            TargetType = "User",
            TargetId = id,
            OldValue = role
        });

        await db.SaveChangesAsync();
        perms.InvalidateCache(id);

        return Ok(new RoleActionDto(id, role));
    }

    // ?? Ad Moderation ??

    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpPost("ads/{id:int}/approve")]
    public async Task<ActionResult<AdStatusActionDto>> ApproveAd(int id, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        var ad = await db.Ads.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (ad == null) return NotFound();

        var actorName = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == actorId)
            .Select(u => u.UserName ?? u.UserLogin)
            .FirstAsync(cancellationToken);

        var old = ad.Status;
        ad.Status = AdStatus.Active;
        ad.RejectionReason = null;
        ad.UpdatedAt = DateTime.UtcNow;

        db.AuditLogs.Add(new AuditLog { ActorUserId = actorId, TargetUserId = ad.UserId, Action = "ad.approve", TargetType = "Ad", TargetId = id, OldValue = old.ToString(), NewValue = "Active" });

        var notification = notificationFactory.CreateAdApproved(ad.UserId, ad.Id, ad.Title, actorName);

        await db.SaveChangesAsync(cancellationToken);

        await notifications.SendAsync(notification, cancellationToken);
        events.Publish(new AdApproved(id, actorId));
        return Ok(new AdStatusActionDto(id, (AdStatus)ad.Status));
    }

    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpPost("ads/{id:int}/reject")]
    public async Task<ActionResult<AdStatusActionDto>> RejectAd(int id, [FromBody] JsonElement body)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        var ad = await db.Ads.FindAsync(id);
        if (ad == null) return NotFound();

        var actorName = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == actorId)
            .Select(u => u.UserName ?? u.UserLogin)
            .FirstAsync();
        var reason = GetString(body, "reason");
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "Не указана" : reason.Trim();
        var rejectedAt = DateTime.UtcNow;

        var old = ad.Status;
        ad.Status = AdStatus.Rejected;
        ad.RejectionReason = normalizedReason;
        ad.UpdatedAt = rejectedAt;

        db.AuditLogs.Add(new AuditLog { ActorUserId = actorId, TargetUserId = ad.UserId, Action = "ad.reject", TargetType = "Ad", TargetId = id, Reason = normalizedReason, OldValue = old.ToString(), NewValue = "Rejected" });

        var notification = notificationFactory.CreateAdRejected(ad.UserId, ad.Id, ad.Title, normalizedReason, actorName);

        await db.SaveChangesAsync();
        await notifications.SendAsync(notification);
        events.Publish(new AdRejected(id, actorId, normalizedReason));
        return Ok(new AdStatusActionDto(id, (AdStatus)ad.Status));
    }

    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpPost("ads/{id:int}/send-to-moderation")]
    public async Task<ActionResult<AdStatusActionDto>> SendToModeration(int id)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        var ad = await db.Ads.FindAsync(id);
        if (ad == null) return NotFound();

        ad.Status = AdStatus.PendingModeration;
        ad.UpdatedAt = DateTime.UtcNow;

        db.AuditLogs.Add(new AuditLog { ActorUserId = actorId, TargetUserId = ad.UserId, Action = "ad.send_to_moderation", TargetType = "Ad", TargetId = id });
        await db.SaveChangesAsync();
        return Ok(new AdStatusActionDto(id, (AdStatus)ad.Status));
    }

    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpDelete("ads/{id:int}")]
    public async Task<ActionResult<AdStatusActionDto>> SoftDeleteAd(int id)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        var ad = await db.Ads.FindAsync(id);
        if (ad == null) return NotFound();

        ad.Status = AdStatus.Deleted;
        ad.DeletedAt = DateTime.UtcNow;
        ad.UpdatedAt = DateTime.UtcNow;

        db.AuditLogs.Add(new AuditLog { ActorUserId = actorId, TargetUserId = ad.UserId, Action = "ad.soft_delete", TargetType = "Ad", TargetId = id });
        await db.SaveChangesAsync();
        return Ok(new AdStatusActionDto(id, (AdStatus)ad.Status));
    }

    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpPost("ads/{id:int}/restore")]
    public async Task<ActionResult<AdStatusActionDto>> RestoreAd(int id)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        var ad = await db.Ads.FindAsync(id);
        if (ad == null) return NotFound();

        ad.Status = AdStatus.PendingModeration;
        ad.DeletedAt = null;
        ad.UpdatedAt = DateTime.UtcNow;

        db.AuditLogs.Add(new AuditLog { ActorUserId = actorId, TargetUserId = ad.UserId, Action = "ad.restore", TargetType = "Ad", TargetId = id });
        await db.SaveChangesAsync();
        return Ok(new AdStatusActionDto(id, (AdStatus)ad.Status));
    }

    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpDelete("ads/{id:int}/hard")]
    public async Task<ActionResult<AdStatusActionDto>> HardDeleteAd(int id)
    {
        if (!User.TryGetUserId(out var actorId)) return Unauthorized();

        var ad = await db.Ads.FindAsync(id);
        if (ad == null) return NotFound();

        db.Ads.Remove(ad);
        db.AuditLogs.Add(new AuditLog { ActorUserId = actorId, TargetUserId = ad.UserId, Action = "ad.hard_delete", TargetType = "Ad", TargetId = id });
        await db.SaveChangesAsync();
        return Ok(new AdStatusActionDto(id, ad.Status));
    }

    private static string? GetString(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static DateTime? GetDateTime(JsonElement body, string name)
    {
        var value = GetString(body, name);
        return value != null && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}

public record RoleDto(string Role);
