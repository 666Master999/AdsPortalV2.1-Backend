using AdsPortalV2.Controllers;
using AdsPortalV2.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Hubs;

[Authorize]
public class NotificationHub(AppDbContext db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        await SendHistory(userId);
        await base.OnConnectedAsync();
    }

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
            .Select(n => new { n.Id, n.Type, n.AdId, n.IsRead, n.CreatedAt })
            .ToListAsync();

        await Clients.Caller.SendAsync("initNotifications", history);
    }
}
