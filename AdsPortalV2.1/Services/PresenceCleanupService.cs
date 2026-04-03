using AdsPortalV2.Data;
using AdsPortalV2.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public class PresenceCleanupService(
    OnlineUserTracker tracker,
    IHubContext<OnlineHub> hub,
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
                await db.Users.Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActivityAt, DateTime.UtcNow), stoppingToken);

                var convIds = await db.Conversations
                    .AsNoTracking()
                    .Where(c => c.SellerId == userId || c.BuyerId == userId)
                    .Select(c => c.Id)
                    .ToListAsync(stoppingToken);

                foreach (var convId in convIds)
                {
                    var conv = await db.Conversations
                        .AsNoTracking()
                        .Where(c => c.Id == convId)
                        .Select(c => new
                        {
                            c.Id,
                            Seller = new { c.Seller.Id, Name = c.Seller.UserName ?? c.Seller.UserLogin },
                            Buyer = new { c.Buyer.Id, Name = c.Buyer.UserName ?? c.Buyer.UserLogin }
                        })
                        .FirstOrDefaultAsync(stoppingToken);

                    if (conv == null) continue;

                    var users = new[] { (Id: conv.Seller.Id, Name: conv.Seller.Name), (Id: conv.Buyer.Id, Name: conv.Buyer.Name) }
                        .Where(p => tracker.IsOnline(p.Id))
                        .Select(p => new { userId = p.Id, userName = p.Name })
                        .ToArray();

                    await hub.Clients.Group($"conversation:{conv.Id}")
                        .SendAsync("chat:onlineUsers", new { conversationId = conv.Id, users }, stoppingToken);
                }
            }
        }
    }
}
