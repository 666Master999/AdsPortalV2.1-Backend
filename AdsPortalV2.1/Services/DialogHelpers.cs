using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using AdsPortalV2.Models;

namespace AdsPortalV2.Services;

public static class DialogHelpers
{
    public const int MaxMessagesPerDialog = 1_000_000;
    public const long MaxFileSizeBytes = 100L * 1024 * 1024;
    public const int MaxSnapshotMessages = 2000;

    public static readonly JsonSerializerOptions s_jsonl = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int FindFileIndex(DialogMeta meta, int messageId)
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

    public static string BuildFileName(int index) => $"dialog_{index}.jsonl";

    public static string BuildLegacyFileName(int index) => $"messages_{index}.jsonl";

    public static string BuildWalFileName() => "dialog.wal.jsonl";

    public static string ResolveFilePath(string folder, int index)
    {
        var modern = Path.Combine(folder, BuildFileName(index));
        if (File.Exists(modern)) return modern;

        var legacy = Path.Combine(folder, BuildLegacyFileName(index));
        return File.Exists(legacy) ? legacy : modern;
    }

    public static string BuildSnapshotPath(string folder) => Path.Combine(folder, "snapshot.json");

    public static string BuildWalPath(string folder) => Path.Combine(folder, BuildWalFileName());

    public static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);
        return items;
    }

    public static void ApplyMessageEvent(Dictionary<int, ChatMessage> messages, ChatMessage entry)
    {
        switch (entry.EventType)
        {
            case DialogMessageEventType.Created:
                messages[entry.Id] = new ChatMessage
                {
                    Id = entry.Id,
                    EventType = entry.EventType,
                    Type = entry.Type,
                    AuthorId = entry.AuthorId,
                    CreatedAt = entry.CreatedAt,
                    Text = entry.Text,
                    Attachments = entry.Attachments != null ? [.. entry.Attachments] : [],
                    ReplyToMessageId = entry.ReplyToMessageId,
                    EditedAt = entry.EditedAt,
                    DeletedAt = entry.DeletedAt
                };
                break;
            case DialogMessageEventType.Edited:
            case DialogMessageEventType.Patched:
                if (!messages.TryGetValue(entry.Id, out var existing)) return;
                if (entry.Text != null) existing.Text = entry.Text;
                if (entry.Attachments != null) existing.Attachments = [.. entry.Attachments];
                if (entry.ReplyToMessageId.HasValue) existing.ReplyToMessageId = entry.ReplyToMessageId;
                existing.EditedAt = entry.EditedAt ?? DateTime.UtcNow;
                existing.EventType = entry.EventType;
                break;
            case DialogMessageEventType.Deleted:
                if (!messages.TryGetValue(entry.Id, out var deleted)) return;
                deleted.DeletedAt = entry.DeletedAt ?? DateTime.UtcNow;
                deleted.Text = null;
                deleted.EventType = entry.EventType;
                break;
        }

    }

    // Map internal ChatMessage -> external ConversationMessageDto
    public static ConversationMessageDto ToConversationMessageDto(int conversationId, ChatMessage msg, MessageAuthorDto? author = null)
    {
        var a = author ?? new MessageAuthorDto(msg.AuthorId, null, string.Empty, null);
        var attachments = (msg.Attachments ?? []).Select(at => new ChatAttachmentDto(at.Url, at.Type, null, null, null, at.Url)).ToList();
        return new ConversationMessageDto(
            conversationId,
            msg.Id,
            msg.Type,
            msg.AuthorId,
            a,
            msg.CreatedAt,
            msg.Text,
            attachments,
            msg.ReplyToMessageId,
            null,
            msg.EditedAt,
            msg.DeletedAt,
            msg.ClientTag,
            false);
    }

    public static async IAsyncEnumerable<ChatMessage> ReadMaterializedMessagesAsync(string folder, DialogMeta meta, int? minMessageId = null, bool useSnapshot = true)
    {
        DialogSnapshot? snapshot = null;
        if (useSnapshot)
            snapshot = await LoadSnapshotAsync(folder, meta);

        var state = new Dictionary<int, ChatMessage>();
        var startIndex = 0;

        if (snapshot != null)
        {
            foreach (var message in snapshot.Messages)
                state[message.Id] = message;

            if (snapshot.LastMessageId > meta.LastMessageId || snapshot.LastFileIndex > meta.LastFileIndex)
            {
                yield break;
            }

            if (minMessageId.HasValue)
            {
                if (minMessageId.Value > snapshot.LastMessageId)
                {
                    state.Clear();
                    startIndex = FindFileIndex(meta, minMessageId.Value);
                }
                else
                {
                    startIndex = snapshot.LastFileIndex;
                }
            }
            else
            {
                startIndex = snapshot.LastFileIndex;
            }
        }
        else
        {
            startIndex = minMessageId.HasValue ? FindFileIndex(meta, minMessageId.Value) : 0;
        }

        for (var i = startIndex; i <= meta.LastFileIndex; i++)
        {
            var path = ResolveFilePath(folder, i);
            if (!File.Exists(path)) continue;
            using var stream = await OpenReadWithRetryAsync(path, CancellationToken.None);
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                ChatMessage? entry = null;
                try { entry = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl); } catch { continue; }
                if (entry == null) continue;

                if (minMessageId.HasValue && entry.Id < minMessageId.Value) continue;

                ApplyMessageEvent(state, entry);
            }
        }

        foreach (var message in state.Values.OrderBy(m => m.Id))
            yield return message;
    }

    public static async Task<FileStream> OpenReadWithRetryAsync(string path, CancellationToken ct = default)
    {
        const int maxAttempts = 5;
        int delay = 50;

        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                );
            }
            catch (IOException) when (i < maxAttempts - 1)
            {
                if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }

        // last attempt - let exceptions bubble
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    public static async Task<DialogSnapshot?> LoadSnapshotAsync(string folder, DialogMeta? meta = null)
    {
        var snapshotPath = BuildSnapshotPath(folder);
        if (!File.Exists(snapshotPath)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(snapshotPath);
            var snap = JsonSerializer.Deserialize<DialogSnapshot>(text, s_jsonl);
            if (snap == null) return null;
            if (meta != null && (snap.LastMessageId > meta.LastMessageId || snap.LastFileIndex > meta.LastFileIndex))
            {
                File.Delete(snapshotPath);
                return null;
            }
            return snap;
        }
        catch
        {
            // corrupted snapshot — ignore
            return null;
        }
    }

    public static async Task SaveSnapshotAsync(string folder, DialogSnapshot snapshot)
    {
        var snapshotPath = BuildSnapshotPath(folder);
        var tmp = snapshotPath + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, s_jsonl);
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, snapshotPath, true);
    }
}
