using System.Text.Json;
using System.Text.Json.Serialization;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;

namespace AdsPortalV2.Services;

public class DialogReaderService(IWebHostEnvironment env)
{
    private static readonly JsonSerializerOptions s_jsonl = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // scroll up: id < beforeId
    public async Task<(List<ChatMessage> Messages, bool HasMore)> GetMessagesAsync(
        Conversation conv, int count, int? beforeId = null)
    {
        var folder = GetFolder(conv);
        if (!Directory.Exists(folder)) return ([], false);

        var meta = await LoadDialogMetaAsync(folder);
        if (meta == null) return ([], false);

        var startFile = beforeId.HasValue ? FindFileIndex(meta, beforeId.Value) : meta.LastFileIndex;
        var result = new List<ChatMessage>(count + 1);

        for (var i = startFile; i >= 0 && result.Count <= count; i--)
        {
            var path = Path.Combine(folder, BuildFileName(i));
            if (!File.Exists(path)) continue;
            var lines = await File.ReadAllLinesAsync(path);
            for (var j = lines.Length - 1; j >= 0 && result.Count <= count; j--)
            {
                if (string.IsNullOrWhiteSpace(lines[j])) continue;
                var msg = JsonSerializer.Deserialize<ChatMessage>(lines[j], s_jsonl);
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

    // pull new: id >= fromMessageId
    public async Task<(List<ChatMessage> Messages, bool HasMore)> GetMessagesSinceAsync(
        Conversation conv, int fromMessageId)
    {
        var folder = GetFolder(conv);
        if (!Directory.Exists(folder)) return ([], false);

        var meta = await LoadDialogMetaAsync(folder);
        if (meta == null) return ([], false);

        var startFile = FindFileIndex(meta, fromMessageId);
        var result = new List<ChatMessage>();

        for (var i = startFile; i <= meta.LastFileIndex; i++)
        {
            var path = Path.Combine(folder, BuildFileName(i));
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

        return (result, false);
    }

    public async Task<ChatMessage?> FindByIdAsync(Conversation conv, int messageId)
    {
        var folder = GetFolder(conv);
        var meta = await LoadDialogMetaAsync(folder);
        if (meta == null) return null;

        var path = Path.Combine(folder, BuildFileName(FindFileIndex(meta, messageId)));
        if (!File.Exists(path)) return null;

        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var msg = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl);
            if (msg?.Id == messageId) return msg;
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

    private async Task<DialogMeta?> LoadDialogMetaAsync(string folder)
    {
        var path = Path.Combine(folder, "dialog.meta.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<DialogMeta>(await File.ReadAllTextAsync(path), s_jsonl);
    }

    // бинарный поиск по FileFirstMessageIds
    private static int FindFileIndex(DialogMeta meta, int messageId)
    {
        var ids = meta.FileFirstMessageIds;
        if (ids.Count == 0) return 0;
        int lo = 0, hi = ids.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ids[mid] <= messageId) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private static string BuildFileName(int index) => $"messages_{index}.jsonl";

    private string GetFolder(Conversation conv)
    {
        var webRoot = env.WebRootPath ?? "wwwroot";
        return Path.Combine(webRoot, conv.DialogFolderPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }
}
