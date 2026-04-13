using AdsPortalV2.Controllers;
using AdsPortalV2.Data;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;
using AdsPortalV2.Entities;

namespace AdsPortalV2.Hubs;

[Authorize]
public class ChatHub(OnlineUserTracker tracker, IServiceScopeFactory scopeFactory, PermissionService perms, DialogReaderService reader, ILogger<ChatHub> logger) : Hub
{
    private static string ConversationGroup(int conversationId) => $"conversation:{conversationId}";
    private readonly ILogger<ChatHub> _logger = logger;
    // Debounce removed: write LastActivityAt immediately on disconnect when user has no more connections.

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        // Subscribe connection to presence group (clients opt-in)

        var becameOnline = tracker.AddConnection(Context.ConnectionId, userId.ToString());

        // Send full presence init to the connecting client
        try
        {
            var onlineUsers = tracker.GetOnlineUserIds();
            await Clients.Caller.SendAsync(HubEvents.PresenceInit, onlineUsers);
        }
        catch { /* best-effort */ }

        // Notify subscribed clients only if user transitioned from offline->online
        if (becameOnline)
        {
            try { await Clients.Group("presence").SendAsync(HubEvents.PresenceOnline, userId.ToString()); } catch { }
            try { await Clients.Group(PresenceUserGroup(userId.ToString())).SendAsync(HubEvents.PresenceOnline, userId.ToString()); } catch { }
        }

        // NOTE: Conversation-specific online projections removed. Presence (tracker) is single source of truth.

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var (isNowOffline, userIdStr) = tracker.RemoveConnection(Context.ConnectionId);

        // Remove all per-user presence subscriptions for this connection
        var subs = tracker.RemoveAllSubscriptions(Context.ConnectionId);
        foreach (var subUserId in subs)
        {
            await Safe(() =>
                Groups.RemoveFromGroupAsync(Context.ConnectionId, PresenceUserGroup(subUserId))
            );
        }

        // Clear active conversation only when the user went fully offline.
        // If the user still has other connections, do not clear here — LeaveConversation should be invoked from client.
        if (!string.IsNullOrEmpty(userIdStr) && isNowOffline)
        {
            if (tracker.TryGetActiveConversation(userIdStr, out var activeConv))
            {
                tracker.ClearActiveConversation(userIdStr);

                // send out presence-left dialog to conversation members
                await Safe(() =>
                    Clients.Group(ConversationGroup(activeConv))
                        .SendAsync(HubEvents.PresenceLeftDialog, new
                        {
                            userId = userIdStr,
                            conversationId = activeConv
                        })
                );
            }
        }

        // Notify offline
        if (isNowOffline && !tracker.IsOnline(userIdStr))
        {
            await Safe(() =>
                Clients.Group("presence").SendAsync(HubEvents.PresenceOffline, userIdStr)
            );

            await Safe(() =>
                Clients.Group(PresenceUserGroup(userIdStr)).SendAsync(HubEvents.PresenceOffline, userIdStr)
            );

            // Persist last activity — log errors, это важно
            if (int.TryParse(userIdStr, out var userIdInt))
            {
                await Safe(() => UpdateLastActivity(userIdInt), log: true);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task Safe(Func<Task> action, bool log = false)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            if (log)
            {
                try { _logger.LogError(ex, "ChatHub safe execution failed"); } catch { /* best-effort */ }
            }
        }
    }

    public Task SubscribePresence() =>
        Groups.AddToGroupAsync(Context.ConnectionId, "presence");

    public Task UnsubscribePresence() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, "presence");

    // Subscribe to presence updates for specific user ids (per-user groups)
    public Task SubscribeToUsers(IReadOnlyCollection<string> userIds)
    {
        var tasks = new List<Task>();
        foreach (var idRaw in userIds)
        {
            var id = idRaw?.Trim();
            if (string.IsNullOrEmpty(id)) continue;

            // Avoid duplicate subscriptions: add to SignalR group only if tracker registers it newly
            if (tracker.RegisterSubscription(Context.ConnectionId, id))
            {
                tasks.Add(Groups.AddToGroupAsync(Context.ConnectionId, PresenceUserGroup(id)));
            }
        }

        return Task.WhenAll(tasks);
    }

    public Task UnsubscribeFromUsers(IReadOnlyCollection<string> userIds)
    {
        var tasks = new List<Task>();
        foreach (var idRaw in userIds)
        {
            var id = idRaw?.Trim();
            if (string.IsNullOrEmpty(id)) continue;

            // Unregister first to ensure tracker state is updated even if group removal fails
            if (tracker.UnregisterSubscription(Context.ConnectionId, id))
            {
                tasks.Add(Groups.RemoveFromGroupAsync(Context.ConnectionId, PresenceUserGroup(id)));
            }
        }

        return Task.WhenAll(tasks);
    }

    private static string PresenceUserGroup(string userId) => $"presence-user-{userId}";

    public Task<IReadOnlyCollection<string>> GetRelevantOnlineUsers(List<string> userIds)
    {
        var result = tracker.GetOnlineUserIds(userIds);
        return Task.FromResult(result);
    }

    // Conversation-scoped presence: user opened the conversation UI
    public async Task EnterConversation(int conversationId)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        if (!await IsConversationParticipantAsync(conversationId, userId)) return;

        // If user was in other conversation, notify that group about leaving
        if (tracker.TryGetActiveConversation(userId.ToString(), out var prevConversationId)
            && prevConversationId != conversationId)
        {
            await Clients.Group(ConversationGroup(prevConversationId))
                .SendAsync(HubEvents.PresenceLeftDialog, new
                {
                    userId = userId.ToString(),
                    conversationId = prevConversationId
                });
        }

        tracker.SetActiveConversation(userId.ToString(), conversationId);

        // provide list of currently active users in this conversation so clients can compute mutual "in-dialog"
        var active = tracker.GetActiveUsersInConversation(conversationId);

        // send initial dialog state to the caller first
        await Clients.Caller.SendAsync(HubEvents.PresenceInitDialog, new
        {
            conversationId,
            activeUsers = active
        });

        await Clients.Group(ConversationGroup(conversationId)).SendAsync(HubEvents.PresenceInDialog, new
        {
            userId = userId.ToString(),
            conversationId,
            activeUsers = active
        });
    }

    // Conversation-scoped presence: user closed the conversation UI
    public async Task LeaveConversation(int conversationId)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        if (!await IsConversationParticipantAsync(conversationId, userId)) return;

        tracker.ClearActiveConversation(userId.ToString());
        // send left + currently active users list
        var active = tracker.GetActiveUsersInConversation(conversationId);
        await Clients.Group(ConversationGroup(conversationId)).SendAsync(HubEvents.PresenceLeftDialog, new
        {
            userId = userId.ToString(),
            conversationId,
            activeUsers = active
        });
    }

    // NOTE: conversation-scoped online projection removed. Clients must filter presence events locally.

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
        if (await perms.HasActiveRestrictionAsync(userId, RestrictionType.ChatBan)) return;
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

        await Clients.Group(ConversationGroup(conversationId)).SendAsync(HubEvents.Read, new
        {
            conversationId,
            userId,
            lastSeenMessageId
        });
    }

    public async Task Received(int conversationId, int lastReceivedMessageId)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        if (!await IsConversationParticipantAsync(conversationId, userId)) return;

        using var scope = scopeFactory.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<DialogWriterService>();
        await writer.ConfirmReceivedAsync(conversationId, userId, lastReceivedMessageId);
    }

    public async Task<IReadOnlyCollection<ChatMessage>> SyncMessages(int conversationId, int lastReceivedMessageId)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return [];
        if (!await IsConversationParticipantAsync(conversationId, userId)) return [];

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && (c.SellerId == userId || c.BuyerId == userId));

        if (conv == null) return [];

        var (messages, _) = await reader.GetMessagesSinceAsync(conv, lastReceivedMessageId + 1);
        return messages;
    }

    public async Task SendMessage(int conversationId, AdsPortalV2.Models.SendMessageRequest dto)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        if (await perms.HasActiveRestrictionAsync(userId, RestrictionType.ChatBan)) return;
        if (!await IsConversationParticipantAsync(conversationId, userId)) return;
        // Respect user blocks: if participants blocked each other, reject and notify caller
        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<MessagePipeline>();
        var ctx = new MessageContext { SenderId = userId, ConversationId = conversationId, Text = dto.Text };
        await pipeline.ExecuteAsync(ctx);

        if (ctx.IsRejected)
        {
            try
            {
                await Clients.Caller.SendAsync("error", new { code = ctx.ErrorCode ?? "blocked", message = "User is blocked" });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send blocked error to caller");
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(dto.Text) && dto.ReplyToMessageId == null && dto.Type == MessageType.Text)
            return;

        var writer = scope.ServiceProvider.GetRequiredService<DialogWriterService>();

        var message = await writer.EnqueueAsync(
            conversationId,
            userId,
            dto.Type,
            dto.Text,
            dto.ReplyToMessageId,
            null,
            dto.ClientTag);
        // Do not broadcast to the conversation group here — writer/controller are responsible
        // for per-recipient delivery that respects per-user deleted markers.
    }

    // replaced by IBlockService

    public async Task Typing(int conversationId)
    {
        if (Context.User?.TryGetUserId(out var userId) != true) return;
        if (!tracker.ShouldBroadcastTyping(userId.ToString(), conversationId)) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payload = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId && (c.SellerId == userId || c.BuyerId == userId))
            .Select(c => new
            {
                conversationId,
                userId,
                userName = c.SellerId == userId
                    ? c.Seller.UserName ?? c.Seller.UserLogin
                    : c.Buyer.UserName ?? c.Buyer.UserLogin
            })
            .FirstOrDefaultAsync();

        if (payload == null) return;

        var excludedConnections = tracker.GetConnections(userId.ToString());
        await Clients.GroupExcept(ConversationGroup(conversationId), excludedConnections).SendAsync(HubEvents.Typing, payload);
    }

    // BroadcastConversationOnlineState removed: presence is single source of truth, clients must filter.

    private async Task<bool> IsConversationParticipantAsync(int conversationId, int userId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Conversations.AnyAsync(c => c.Id == conversationId && (c.SellerId == userId || c.BuyerId == userId));
    }
}
