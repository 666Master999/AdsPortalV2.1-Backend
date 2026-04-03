using AdsPortalV2.Controllers;
using AdsPortalV2.Data;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AdsPortalV2.Hubs;

[Authorize]
public class OnlineHub(OnlineUserTracker tracker, IServiceScopeFactory scopeFactory) : Hub
{
    private static string ConversationGroup(int conversationId) => $"conversation:{conversationId}";
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingOfflineDebounce = new();
    private const int OfflineDebounceMs = 2000; // Увеличено для предотвращения фликера при плохой сети

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;

        // ✅ Отмена старого debounce при реконнекте
        if (_pendingOfflineDebounce.TryRemove(userId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");

        tracker.AddConnection(Context.ConnectionId, userId);

        // Broadcast bulk online state for all conversations of this user (exclude own connections)
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var convIds = await db.Conversations
                .AsNoTracking()
                .Where(c => c.SellerId == userId || c.BuyerId == userId)
                .Select(c => c.Id)
                .ToListAsync();

            var exclude = tracker.GetConnections(userId);
            foreach (var convId in convIds)
                await BroadcastConversationOnlineState(convId, exclude);
        }
        catch { /* best-effort */ }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var (isNowOffline, userId) = tracker.RemoveConnection(Context.ConnectionId);
        if (isNowOffline)
        {
            // ✅ Сразу обновляем UI
            await BroadcastUserConversations(userId);

            // ✅ Debounce только для обновления LastActivity
            var cts = new CancellationTokenSource();
            if (!_pendingOfflineDebounce.TryAdd(userId, cts))
            {
                cts.Dispose();
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(OfflineDebounceMs, cts.Token);

                        if (!cts.Token.IsCancellationRequested && !tracker.IsOnline(userId))
                        {
                            await UpdateLastActivity(userId);
                        }
                    }
                    catch (TaskCanceledException) { }
                    catch { /* best-effort */ }
                    finally
                    {
                        if (_pendingOfflineDebounce.TryRemove(userId, out var removed))
                        {
                            try { removed.Dispose(); } catch { }
                        }
                    }
                });
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task BroadcastUserConversations(int userId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var convIds = await db.Conversations
            .AsNoTracking()
            .Where(c => c.SellerId == userId || c.BuyerId == userId)
            .Select(c => c.Id)
            .ToListAsync();

        foreach (var convId in convIds)
        {
            await BroadcastConversationOnlineState(convId, null);
        }
    }

    private async Task UpdateLastActivity(int userId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActivityAt, DateTime.UtcNow));
    }

    public Task Ping()
    {
        tracker.Ping(Context.ConnectionId);
        return Task.CompletedTask;
    }

    public async Task JoinGroup(int conversationId)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        if (!await IsConversationParticipantAsync(conversationId, userId)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
    }

    public Task LeaveGroup(int conversationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));

    public async Task Read(int conversationId, int lastSeenMessageId)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        if (!await IsConversationParticipantAsync(conversationId, userId)) return;

        using var scope = scopeFactory.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<DialogWriterService>();
        await writer.MarkAsReadAsync(conversationId, userId, lastSeenMessageId);

        await Clients.Group(ConversationGroup(conversationId)).SendAsync("chat:read", new
        {
            conversationId,
            userId,
            lastSeenMessageId
        });
    }

    public async Task Typing(int conversationId)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        if (!tracker.ShouldBroadcastTyping(userId, conversationId)) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payload = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId && (c.SellerId == userId || c.BuyerId == userId))
            .Select(c => new
            {
                conversationId,
                userId = userId,
                userName = c.SellerId == userId
                    ? c.Seller.UserName ?? c.Seller.UserLogin
                    : c.Buyer.UserName ?? c.Buyer.UserLogin
            })
            .FirstOrDefaultAsync();

        if (payload == null) return;

        var excludedConnections = tracker.GetConnections(userId);
        await Clients.GroupExcept(ConversationGroup(conversationId), excludedConnections).SendAsync("chat:typing", payload);
    }

    private async Task BroadcastConversationOnlineState(int conversationId, IReadOnlyList<string>? excludeConnectionIds)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conv = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new
            {
                c.Id,
                Seller = new { c.Seller.Id, Name = c.Seller.UserName ?? c.Seller.UserLogin },
                Buyer = new { c.Buyer.Id, Name = c.Buyer.UserName ?? c.Buyer.UserLogin }
            })
            .FirstOrDefaultAsync();

        if (conv == null) return;

        var participants = new[] { (conv.Seller.Id, conv.Seller.Name), (conv.Buyer.Id, conv.Buyer.Name) };
        var users = participants
            .Where(p => tracker.IsOnline(p.Item1))
            .Select(p => new { userId = p.Item1, userName = p.Item2 })
            .ToArray();

        var payload = new { conversationId = conv.Id, users };

        if (excludeConnectionIds != null && excludeConnectionIds.Count > 0)
            await Clients.GroupExcept(ConversationGroup(conversationId), excludeConnectionIds).SendAsync("chat:onlineUsers", payload);
        else
            await Clients.Group(ConversationGroup(conversationId)).SendAsync("chat:onlineUsers", payload);
    }

    private async Task<bool> IsConversationParticipantAsync(int conversationId, int userId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Conversations.AnyAsync(c => c.Id == conversationId && (c.SellerId == userId || c.BuyerId == userId));
    }
}
