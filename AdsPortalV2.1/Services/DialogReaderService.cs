using System.Text.Json;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;

namespace AdsPortalV2.Services;

public class DialogReaderService(IWebHostEnvironment env)
{
    private static readonly JsonSerializerOptions s_jsonl = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<(List<ChatMessage> Messages, bool HasMore)> GetMessagesAsync(
        Conversation conv, int count, int? beforeId = null)
    {
        var folder = GetFolder(conv);
        if (!Directory.Exists(folder)) return ([], false);

        var startCluster = conv.LastClusterId;

        if (beforeId.HasValue)
        {
            int lo = 0, hi = conv.LastClusterId;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var metaPath = Path.Combine(folder, $"cluster_{mid}.meta.json");
                if (!File.Exists(metaPath)) { lo = mid + 1; continue; }
                var meta = JsonSerializer.Deserialize<ClusterMeta>(await File.ReadAllTextAsync(metaPath), s_jsonl)!;
                if (beforeId.Value < meta.FirstMessageId) { hi = mid - 1; continue; }
                if (beforeId.Value > meta.LastMessageId) { lo = mid + 1; continue; }
                startCluster = mid;
                break;
            }
        }

        var result = new List<ChatMessage>(count + 1);

        for (var clusterId = startCluster; clusterId >= 0 && result.Count <= count; clusterId--)
        {
            var path = Path.Combine(folder, BuildJsonlName(clusterId, conv));
            if (!File.Exists(path)) continue;

            var lines = await File.ReadAllLinesAsync(path);
            for (var i = lines.Length - 1; i >= 0 && result.Count <= count; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var msg = JsonSerializer.Deserialize<ChatMessage>(lines[i], s_jsonl);
                if (msg == null || (beforeId.HasValue && msg.Id >= beforeId.Value)) continue;
                if (msg.DeletedAt.HasValue) msg.Text = null;
                result.Add(msg);
            }
        }

        var hasMore = result.Count > count;
        if (hasMore) result.RemoveAt(result.Count - 1);
        result.Reverse();
        return (result, hasMore);
    }

    public async Task<(List<ChatMessage> Messages, bool HasMore)> GetMessagesSinceAsync(Conversation conv, int fromMessageId)
    {
        var folder = GetFolder(conv);
        if (!Directory.Exists(folder)) return ([], false);

        var startCluster = await FindClusterIdAsync(folder, fromMessageId, conv);
        var result = new List<ChatMessage>();

        for (var clusterId = startCluster; clusterId <= conv.LastClusterId; clusterId++)
        {
            var path = Path.Combine(folder, BuildJsonlName(clusterId, conv));
            if (!File.Exists(path)) continue;

            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var msg = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl);
                if (msg == null || msg.Id < fromMessageId) continue;
                if (msg.DeletedAt.HasValue) msg.Text = null;
                result.Add(msg);
            }
        }

        return (result, fromMessageId > 1);
    }

    public async Task<ChatMessage?> FindByIdAsync(Conversation conv, int messageId)
    {
        var folder = GetFolder(conv);

        // Бинарный поиск по cluster meta
        int lo = 0, hi = conv.LastClusterId;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var metaPath = Path.Combine(folder, $"cluster_{mid}.meta.json");
            if (!File.Exists(metaPath)) { lo = mid + 1; continue; }

            var meta = JsonSerializer.Deserialize<ClusterMeta>(
                await File.ReadAllTextAsync(metaPath), s_jsonl)!;

            if (messageId < meta.FirstMessageId) { hi = mid - 1; continue; }
            if (messageId > meta.LastMessageId) { lo = mid + 1; continue; }

            var path = Path.Combine(folder, BuildJsonlName(mid, conv));
            if (!File.Exists(path)) return null;

            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var msg = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl);
                if (msg?.Id == messageId) return msg;
            }
            return null;
        }

        return null;
    }

    public async Task<List<ChatMessage>> GetLastMessagesWithUserNamesAsync(AppDbContext db, Conversation conv, int count = 50)
    {
        var (messages, _) = await GetMessagesAsync(conv, count);

        foreach (var message in messages)
        {
            var user = await db.Users.FindAsync(message.AuthorId);
            message.Text = user != null ? $"{user.UserName ?? user.UserLogin}: {message.Text}" : message.Text;
        }

        return messages;
    }

    private string GetFolder(Conversation conv)
    {
        var webRoot = env.WebRootPath ?? "wwwroot";
        return Path.Combine(
            webRoot, conv.DialogFolderPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task<int> FindClusterIdAsync(string folder, int messageId, Conversation conv)
    {
        int lo = 0, hi = conv.LastClusterId;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var metaPath = Path.Combine(folder, $"cluster_{mid}.meta.json");
            if (!File.Exists(metaPath)) { lo = mid + 1; continue; }
            var meta = JsonSerializer.Deserialize<ClusterMeta>(await File.ReadAllTextAsync(metaPath), s_jsonl)!;

            if (messageId < meta.FirstMessageId) { hi = mid - 1; continue; }
            if (messageId > meta.LastMessageId) { lo = mid + 1; continue; }
            return mid;
        }

        return Math.Clamp(lo, 0, conv.LastClusterId);
    }

    private static string BuildJsonlName(int clusterId, Conversation conv) =>
        $"{clusterId}_{conv.AdId}_{conv.SellerId}_{conv.BuyerId}.jsonl";
}
