using System.Text.Json;
using System.Diagnostics;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;
using static AdsPortalV2.Services.DialogHelpers;

namespace AdsPortalV2.Services;

public class DialogReaderService(IWebHostEnvironment env, DialogWriterService writer, ILogger<DialogReaderService> logger)
{
    // scroll up: id < beforeId
    public async Task<(List<ChatMessage> Messages, bool HasMore)> GetMessagesAsync(
        Conversation conv, int count, int? beforeId = null)
    {
        var cached = writer.GetLastMessagesFromCache(conv.Id, count, beforeId);
        if (cached.HasValue) return cached.Value;

        var folder = GetFolder(conv);
        if (!Directory.Exists(folder)) return ([], false);

        var meta = await LoadDialogMetaAsync(folder);
        if (meta == null) return ([], false);

        var sw = Stopwatch.StartNew();
        var all = await ToListAsync(ReadMaterializedMessagesAsync(folder, meta));
        var ordered = beforeId.HasValue
            ? all.Where(m => m.Id < beforeId.Value).OrderBy(m => m.Id).ToList()
            : all.OrderBy(m => m.Id).ToList();

        var hasMore = ordered.Count > count;
        var result = hasMore ? ordered.TakeLast(count).ToList() : ordered;
        sw.Stop();
        logger.LogInformation("tail read for conversation {ConversationId} took {ElapsedMs} ms", conv.Id, sw.ElapsedMilliseconds);

        // If this is the last page (opening chat), populate last messages cache for faster subsequent reads
        if (beforeId == null)
        {
            writer.PopulateLastMessagesCache(conv.Id, result);
        }
        return (result, hasMore);
    }

    // pull new: id >= fromMessageId
    public async Task<(List<ChatMessage> Messages, bool HasMore)> GetMessagesSinceAsync(
        Conversation conv, int fromMessageId)
    {
        var cachedSince = writer.GetMessagesSinceFromCache(conv.Id, fromMessageId);
        if (cachedSince.HasValue)
        {
            return cachedSince.Value;
        }

        var folder = GetFolder(conv);
        if (!Directory.Exists(folder)) return ([], false);

        var meta = await LoadDialogMetaAsync(folder);
        if (meta == null) return ([], false);

        var sw = Stopwatch.StartNew();
        var result = await ToListAsync(ReadMaterializedMessagesAsync(folder, meta, fromMessageId));
        result = [.. result.Where(m => m.Id >= fromMessageId).OrderBy(m => m.Id)];
        sw.Stop();
        logger.LogInformation("since read for conversation {ConversationId} took {ElapsedMs} ms", conv.Id, sw.ElapsedMilliseconds);

        // populate cache only if this is a recent result near the tail
        if (result.Count > 0 && meta.LastMessageId - result[^1].Id < writer.LastMessagesCacheCapacity)
        {
            writer.PopulateLastMessagesCache(conv.Id, result);
        }

        return (result, false);
    }

    public async Task<ChatMessage?> FindByIdAsync(Conversation conv, int messageId)
    {
        var folder = GetFolder(conv);
        var meta = await LoadDialogMetaAsync(folder);
        if (meta == null) return null;

        await foreach (var msg in ReadMaterializedMessagesAsync(folder, meta, messageId))
        {
            if (msg.Id == messageId)
                return msg;
        }

        return null;
    }

    public async Task<int?> GetLatestMessageIdAsync(Conversation conv)
    {
        var folder = GetFolder(conv);
        var meta = await LoadDialogMetaAsync(folder);
        return meta?.LastMessageId;
    }

    public async Task<List<ChatMessage>> GetLastMessagesWithUserNamesAsync(AppDbContext db, Conversation conv, int count = 50)
    {
        var (messages, _) = await GetMessagesAsync(conv, count);

        var authorIds = messages
            .Select(message => message.AuthorId)
            .Distinct()
            .ToList();

        var users = await db.Users
            .AsNoTracking()
            .Where(user => authorIds.Contains(user.Id))
            .Select(user => new { user.Id, user.UserName, user.UserLogin })
            .ToListAsync();

        var userNames = users.ToDictionary(
            user => user.Id,
            user => user.UserName ?? user.UserLogin);

        return messages.Select(message => new ChatMessage
        {
            Id = message.Id,
            Type = message.Type,
            AuthorId = message.AuthorId,
            CreatedAt = message.CreatedAt,
            Text = userNames.TryGetValue(message.AuthorId, out var userName)
                ? $"{userName}: {message.Text}"
                : message.Text,
            Attachments = message.Attachments != null
                ? [.. message.Attachments]
                : [],
            ReplyToMessageId = message.ReplyToMessageId,
            EditedAt = message.EditedAt,
            DeletedAt = message.DeletedAt
        }).ToList();
    }

    private static async Task<DialogMeta?> LoadDialogMetaAsync(string folder)
    {
        var path = Path.Combine(folder, "dialog.meta.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<DialogMeta>(await File.ReadAllTextAsync(path), s_jsonl);
    }

    private string GetFolder(Conversation conv)
    {
        var webRoot = env.WebRootPath ?? "wwwroot";
        return Path.Combine(webRoot, conv.DialogFolderPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }
}
