using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AdsPortalV2.Services;

public static class AuthorizationPolicies
{
    public const string CanViewHiddenAd = nameof(CanViewHiddenAd);
    public const string CanModerateAd = nameof(CanModerateAd);
    public const string CanBanUser = nameof(CanBanUser);
    public const string CanEditAd = nameof(CanEditAd);
    public const string CanDeleteAd = nameof(CanDeleteAd);
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class CanEditAdRequirement : IAuthorizationRequirement;
public sealed class CanDeleteAdRequirement : IAuthorizationRequirement;

public class PermissionRequirementHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Claims.Any(c => c.Type == "perm" && c.Value == requirement.Permission))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public class CanEditAdHandler(PermissionService perms) : AuthorizationHandler<CanEditAdRequirement, Ad>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CanEditAdRequirement requirement, Ad resource)
    {
        if (!context.User.TryGetUserId(out var userId))
            return;

        var uctx = await perms.GetUserContextAsync(userId);
        if (uctx.Restrictions.Any(r => r.Type == RestrictionType.PostBan))
            return;

        var canEditOwn = resource.UserId == userId;
        var canModerate = uctx.Permissions.Contains("ads.moderate");

        if (canEditOwn || canModerate)
            context.Succeed(requirement);
    }
}

public class CanDeleteAdHandler(PermissionService perms) : AuthorizationHandler<CanDeleteAdRequirement, Ad>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CanDeleteAdRequirement requirement, Ad resource)
    {
        if (!context.User.TryGetUserId(out var userId))
            return;

        var uctx = await perms.GetUserContextAsync(userId);
        if (uctx.Restrictions.Any(r => r.Type == RestrictionType.PostBan))
            return;

        var canDeleteOwn = resource.UserId == userId;
        var canModerate = uctx.Permissions.Contains("ads.moderate");

        if (canDeleteOwn || canModerate)
            context.Succeed(requirement);
    }
}
