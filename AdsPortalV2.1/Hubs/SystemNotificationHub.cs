using AdsPortalV2.Controllers;
using AdsPortalV2.Data;
using AdsPortalV2.Services;
using AdsPortalV2.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Hubs;

[Authorize]
public class SystemNotificationHub(AppDbContext db, PermissionService perms) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        await SendHistory(userId);
        await base.OnConnectedAsync();
    }

    public async Task JoinConversation(int conversationId)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        // Prevent banned users from joining conversation groups
        if (await perms.HasActiveRestrictionAsync(userId, RestrictionType.ChatBan)) return;
        var ok = await db.Conversations.AnyAsync(c => c.Id == conversationId && (c.SellerId == userId || c.BuyerId == userId));
        if (ok) await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation:{conversationId}");
    }

    public Task LeaveConversation(int conversationId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"conversation:{conversationId}");

    public async Task RequestNotifications()
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        await SendHistory(userId);
    }

    private async Task SendHistory(int userId)
    {
        var history = await db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(50)
            .ToListAsync();

        var adIds = history
            .Where(n => n.AdId.HasValue)
            .Select(n => n.AdId!.Value)
            .Distinct()
            .ToList();

        var imagesByAdId = adIds.Count == 0
            ? []
            : await db.Ads
                .AsNoTracking()
                .Where(a => adIds.Contains(a.Id))
                .Select(a => new
                {
                    a.Id,
                    Image = db.AdImages
                        .Where(i => i.Id == a.MainImageId)
                        .Select(i => i.FilePath)
                        .FirstOrDefault(),
                    a.Title
                })
                .ToDictionaryAsync(x => x.Id, x => x.Image);

        var titlesByAdId = adIds.Count == 0
            ? []
            : await db.Ads
                .AsNoTracking()
                .Where(a => adIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Title })
                .ToDictionaryAsync(x => x.Id, x => x.Title);

        var dto = history
            .Select(n => NotificationMapper.ToDto(
                n,
                n.AdId.HasValue && imagesByAdId.TryGetValue(n.AdId.Value, out var image) ? image : null,
                n.AdId.HasValue && titlesByAdId.TryGetValue(n.AdId.Value, out var title) ? title : null))
            .ToList();
        await Clients.Caller.SendAsync(HubEvents.InitNotifications, dto);
    }
}
