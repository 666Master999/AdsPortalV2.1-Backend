using System.Collections.Concurrent;

namespace AdsPortalV2.Services;

public class OnlineUserTracker
{
    internal static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan TypingThrottle = TimeSpan.FromMilliseconds(500);

    // Защищает атомарность AddConnection/RemoveConnection
    private readonly Lock _lock = new();

    // connectionId → (userId, lastPing)
    private readonly ConcurrentDictionary<string, (int UserId, DateTime LastPing)> _connections = new();
    // userId → кол-во активных соединений (O(1) IsOnline)
    private readonly ConcurrentDictionary<int, int> _userConnectionCount = new();
    private readonly ConcurrentDictionary<(int UserId, int ConversationId), DateTime> _typingEvents = new();

    /// <summary>Returns true if user was offline before this connection (should notify UserOnline).</summary>
    public bool AddConnection(string connectionId, int userId)
    {
        lock (_lock)
        {
            _connections[connectionId] = (userId, DateTime.UtcNow);
            return _userConnectionCount.AddOrUpdate(userId, 1, (_, c) => c + 1) == 1;
        }
    }

    /// <summary>Returns true if user has no more active connections (should notify UserOffline).</summary>
    public (bool IsNowOffline, int UserId) RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryRemove(connectionId, out var entry)) return (false, 0);
            var count = _userConnectionCount.AddOrUpdate(entry.UserId, 0, (_, c) => c - 1);
            if (count <= 0) _userConnectionCount.TryRemove(entry.UserId, out _);
            return (count <= 0, entry.UserId);
        }
    }

    public void Ping(string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
            _connections[connectionId] = (entry.UserId, DateTime.UtcNow);
    }

    // O(1) — чтение из счётчика, без итерации по всем соединениям
    public bool IsOnline(int userId) =>
        _userConnectionCount.TryGetValue(userId, out var count) && count > 0;

    // O(N) по входному списку
    public HashSet<int> GetOnlineUserIds(IEnumerable<int> userIds) =>
        [.. userIds.Where(IsOnline)];

    public IReadOnlyList<string> GetConnections(int userId) =>
        [.. _connections.Where(x => x.Value.UserId == userId).Select(x => x.Key)];

    public bool ShouldBroadcastTyping(int userId, int conversationId)
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
    public IReadOnlyList<int> CleanStaleConnections()
    {
        var cutoff = DateTime.UtcNow - PingTimeout;
        var stale = _connections.Where(kv => kv.Value.LastPing < cutoff).Select(kv => kv.Key).ToList();
        var offlineUsers = new List<int>();

        foreach (var connId in stale)
        {
            var (isNowOffline, userId) = RemoveConnection(connId);
            if (isNowOffline) offlineUsers.Add(userId);
        }

        return offlineUsers.Distinct().ToList();
    }
}
