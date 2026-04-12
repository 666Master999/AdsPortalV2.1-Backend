using AdsPortalV2.Data;
using AdsPortalV2.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public class PresenceCleanupService(
    OnlineUserTracker tracker,
    IHubContext<ChatHub> hub,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var offlineUsers = tracker.CleanStaleConnections();
            if (offlineUsers.Count == 0) continue;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var userId in offlineUsers)
            {
                // offlineUsers are string ids; try parse to int for DB update
                if (int.TryParse(userId, out var uid))
                {
                    await db.Users.Where(u => u.Id == uid)
                        .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActivityAt, DateTime.UtcNow), stoppingToken);
                }
                // Do not emit presence events from cleanup. Presence events are emitted on real disconnects.
                // Cleanup only updates DB state (LastActivityAt) to avoid duplicate/incorrect presence events.
            }
        }
    }
}
