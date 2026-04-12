using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace AdsPortalV2.Services;

public class OnlineUserTracker
{
    // IMPORTANT:
    // OnlineUserTracker is the SINGLE source of truth for presence.
    // All realtime presence events and projections must be derived from this tracker.
    // Do NOT duplicate presence state elsewhere; use this tracker for online checks and derived views.

    internal static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan TypingThrottle = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan ActiveConversationTimeout = TimeSpan.FromSeconds(60);

    // Защищает атомарность AddConnection/RemoveConnection
    private readonly Lock _lock = new();

    // connectionId → connection info (reference type to allow atomic ping updates)
    private class ConnectionInfo
    {
        public string UserId;
        public long LastPingTicks;

        public ConnectionInfo(string userId, long lastPingTicks)
        {
            UserId = userId;
            LastPingTicks = lastPingTicks;
        }
    }

    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
    // userId -> set of connectionIds for O(1) per-user connection lookup (userId is string)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connectionsByUser = new();
    // userId → кол-во активных соединений (O(1) IsOnline) (userId is string)
    private readonly ConcurrentDictionary<string, int> _userConnectionCount = new();
    private readonly ConcurrentDictionary<(string UserId, int ConversationId), DateTime> _typingEvents = new();
    // userId -> (conversationId, lastSeen) (userId is string)
    private readonly ConcurrentDictionary<string, (int ConversationId, DateTime LastSeen)> _userActiveConversation = new();
    // connectionId -> subscribed userIds (for per-user presence subscriptions)
    // NOTE: userIds for presence are strings (not ints)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _subscriptions = new();

    /// <summary>Returns true if user was offline before this connection (should notify UserOnline).</summary>
    public bool AddConnection(string connectionId, string userId)
    {
        lock (_lock)
        {
            _connections[connectionId] = new ConnectionInfo(userId, DateTime.UtcNow.Ticks);
            var set = _connectionsByUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>());
            set[connectionId] = 0;
            return _userConnectionCount.AddOrUpdate(userId, 1, (_, c) => c + 1) == 1;
        }
    }

    /// <summary>Returns true if user has no more active connections (should notify UserOffline).</summary>
    public (bool IsNowOffline, string UserId) RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryRemove(connectionId, out var entry)) return (false, null);
            if (_connectionsByUser.TryGetValue(entry.UserId, out var set))
            {
                set.TryRemove(connectionId, out _);
                if (set.IsEmpty) _connectionsByUser.TryRemove(entry.UserId, out _);
            }

            var count = _userConnectionCount.AddOrUpdate(entry.UserId, 0, (_, c) => Math.Max(0, c - 1));
            if (count <= 0) _userConnectionCount.TryRemove(entry.UserId, out _);
            return (count <= 0, entry.UserId);
        }
    }

    public void Ping(string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
        {
            // update last ping ticks atomically on the reference object
            Volatile.Write(ref entry.LastPingTicks, DateTime.UtcNow.Ticks);

            // Refresh active conversation TTL for this user (if any)
            if (_userActiveConversation.TryGetValue(entry.UserId, out var conv))
            {
                _userActiveConversation[entry.UserId] = (conv.ConversationId, DateTime.UtcNow);
            }
        }
    }

    public IReadOnlyCollection<string> GetOnlineUserIds()
    {
        return _userConnectionCount.Keys.ToArray();
    }

    // O(1) — чтение из счётчика, без итерации по всем соединениям
    public bool IsOnline(string userId) =>
        _userConnectionCount.TryGetValue(userId, out var count) && count > 0;

    // O(N) по входному списку
    public IReadOnlyCollection<string> GetOnlineUserIds(IEnumerable<string> userIds) =>
        userIds.Where(IsOnline).ToArray();

    // Subscription management (per-connection) - userId is string
    public bool RegisterSubscription(string connectionId, string userId)
    {
        var set = _subscriptions.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, byte>());
        // return true only if subscription was newly added
        return set.TryAdd(userId, 0);
    }

    public bool UnregisterSubscription(string connectionId, string userId)
    {
        if (_subscriptions.TryGetValue(connectionId, out var set))
        {
            var removed = set.TryRemove(userId, out _);
            if (set.IsEmpty) _subscriptions.TryRemove(connectionId, out _);
            return removed;
        }

        return false;
    }

    /// <summary>Removes and returns all subscriptions for the given connectionId.</summary>
    public IReadOnlyCollection<string> RemoveAllSubscriptions(string connectionId)
    {
        if (_subscriptions.TryRemove(connectionId, out var set))
        {
            return set.Keys.ToArray();
        }

        return Array.Empty<string>();
    }

    public IReadOnlyList<string> GetConnections(string userId)
    {
        if (_connectionsByUser.TryGetValue(userId, out var set))
        {
            return set.Keys.ToArray();
        }
        return Array.Empty<string>();
    }

    // Active conversation tracking (in-dialog) with TTL
    public void SetActiveConversation(string userId, int conversationId)
    {
        _userActiveConversation[userId] = (conversationId, DateTime.UtcNow);
    }

    public void ClearActiveConversation(string userId)
    {
        _userActiveConversation.TryRemove(userId, out _);
    }

    public bool TryGetActiveConversation(string userId, out int conversationId)
    {
        if (_userActiveConversation.TryGetValue(userId, out var entry))
        {
            if (DateTime.UtcNow - entry.LastSeen < ActiveConversationTimeout)
            {
                conversationId = entry.ConversationId;
                return true;
            }

            // stale
            _userActiveConversation.TryRemove(userId, out _);
        }

        conversationId = 0;
        return false;
    }

    public bool ShouldBroadcastTyping(string userId, int conversationId)
    {
        var now = DateTime.UtcNow;
        var allowed = false;

        _typingEvents.AddOrUpdate(
            (userId, conversationId),
            _ =>
            {
                allowed = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last < TypingThrottle) return last;
                allowed = true;
                return now;
            });

        return allowed;
    }

    /// <summary>Removes stale connections. Returns distinct userIds that just went offline.</summary>
    public IReadOnlyList<string> CleanStaleConnections()
    {
        // To avoid races with Ping(), take snapshot of keys and re-check LastPingTicks under TryGetValue
        var cutoffTicks = DateTime.UtcNow.Add(-PingTimeout).Ticks;
        var staleCandidates = _connections.Keys.ToList();
        var stale = new List<string>();
        foreach (var connId in staleCandidates)
        {
            if (_connections.TryGetValue(connId, out var entry))
            {
                var last = Volatile.Read(ref entry.LastPingTicks);
                if (last < cutoffTicks) stale.Add(connId);
            }
        }
        var offlineUsers = new List<string>();

        foreach (var connId in stale)
        {
            var (isNowOffline, userId) = RemoveConnection(connId);
            if (isNowOffline) offlineUsers.Add(userId);
        }

        return offlineUsers.Distinct().ToArray();
    }
}
