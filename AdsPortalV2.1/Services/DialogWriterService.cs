using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Hubs;
using AdsPortalV2.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public class DialogWriterService(IServiceScopeFactory scopeFactory, IWebHostEnvironment env, IHubContext<NotificationHub> hub) : IHostedService
{
    private static readonly JsonSerializerOptions s_jsonl = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly Channel<WriteCommand> _channel = Channel.CreateUnbounded<WriteCommand>();
    private readonly Dictionary<int, int> _activeFileIndexes = new();
    // LRU: max 4000 metadata entries (unread + dialog meta), max 1000 message caches
    private readonly MemoryCache _metaCache = new(new MemoryCacheOptions { SizeLimit = 4_000 });
    private readonly MemoryCache _msgCache = new(new MemoryCacheOptions { SizeLimit = 1_000 });
    private const int MaxCachedMessages = 100;
    private static MemoryCacheEntryOptions MetaOpts => new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(30)).SetSize(1);
    private static MemoryCacheEntryOptions MsgOpts => new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(60)).SetSize(1);
    private Task? _writerTask;

    public Task StartAsync(CancellationToken ct)
    {
        _writerTask = Task.Run(() => ProcessAsync(ct), ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _channel.Writer.Complete();
        if (_writerTask != null) await _writerTask;
    }

    public Task<ChatMessage> EnqueueAsync(
        int conversationId, int authorId, MessageType type, string? text,
        int? replyToId = null, List<ChatAttachment>? attachments = null)
        => Enqueue(new WriteCommand(WriteKind.Send, conversationId, authorId, type, text, replyToId, null, attachments, new()));

    public Task<ChatMessage> EnqueueEditAsync(int conversationId, int messageId, string? newText)
        => Enqueue(new WriteCommand(WriteKind.Edit, conversationId, 0, default, newText, null, messageId, null, new()));

    public Task<ChatMessage> EnqueueDeleteAsync(int conversationId, int messageId)
        => Enqueue(new WriteCommand(WriteKind.Delete, conversationId, 0, default, null, null, messageId, null, new()));

    public Task<ChatMessage> EnqueuePatchAsync(int conversationId, int messageId, string? text, List<ChatAttachment>? attachments)
        => Enqueue(new WriteCommand(WriteKind.Patch, conversationId, 0, default, text, null, messageId, attachments, new()));

    // сохраняем только позицию чтения; unread считается по сообщениям оппонента после неё
    public async Task MarkAsReadAsync(int conversationId, int userId, int lastSeenMessageId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = await db.Conversations.FindAsync(conversationId)
            ?? throw new InvalidOperationException("Conversation not found");

        var folder = GetFolder(conv);
        var unreadMeta = await LoadUnreadMetaAsync(folder);

        var current = userId == conv.SellerId ? unreadMeta.SellerLastSeenMessageId : unreadMeta.BuyerLastSeenMessageId;
        if (current >= lastSeenMessageId) return;

        if (userId == conv.SellerId) unreadMeta.SellerLastSeenMessageId = lastSeenMessageId;
        else if (userId == conv.BuyerId) unreadMeta.BuyerLastSeenMessageId = lastSeenMessageId;
        else return;

        await SaveUnreadMetaAsync(folder, unreadMeta);

        var dialogMeta = await LoadDialogMetaAsync(folder);
        conv.HasUnreadForSeller = dialogMeta != null && (await GetUnreadStateAsync(folder, dialogMeta, unreadMeta.SellerLastSeenMessageId ?? 0, conv.SellerId)).Count > 0;
        conv.HasUnreadForBuyer = dialogMeta != null && (await GetUnreadStateAsync(folder, dialogMeta, unreadMeta.BuyerLastSeenMessageId ?? 0, conv.BuyerId)).Count > 0;
        await db.SaveChangesAsync();
    }

    public async Task<(int Count, int? FirstUnreadMessageId)> GetUnreadStateAsync(Conversation conv, int userId)
        => await GetUnreadStateAsync(conv.DialogFolderPath, conv.SellerId, conv.BuyerId, userId);

    public async Task<(int Count, int? FirstUnreadMessageId)> GetUnreadStateAsync(
        string dialogFolderPath, int sellerId, int buyerId, int userId)
    {
        var folder = GetFolder(dialogFolderPath);
        var unreadMeta = await LoadUnreadMetaAsync(folder);
        var dialogMeta = await LoadDialogMetaAsync(folder);
        if (dialogMeta == null) return (0, null);

        var lastSeen = userId == sellerId
            ? unreadMeta.SellerLastSeenMessageId ?? 0
            : userId == buyerId
                ? unreadMeta.BuyerLastSeenMessageId ?? 0
                : dialogMeta.LastMessageId;

        return await GetUnreadStateAsync(folder, dialogMeta, lastSeen, userId);
    }

    public async Task<int?> GetLastSeenMessageIdAsync(Conversation conv, int userId)
    {
        var meta = await LoadUnreadMetaAsync(GetFolder(conv));
        return userId == conv.SellerId ? meta.SellerLastSeenMessageId : meta.BuyerLastSeenMessageId;
    }

    private async Task<(int Count, int? FirstUnreadMessageId)> GetUnreadStateAsync(string folder, DialogMeta dialogMeta, int lastSeenMessageId, int userId)
    {
        if (dialogMeta.LastMessageId <= lastSeenMessageId) return (0, null);

        var startFileIndex = FindFileIndex(dialogMeta, lastSeenMessageId + 1);
        var count = 0;
        int? firstUnreadMessageId = null;

        for (var i = startFileIndex; i <= dialogMeta.LastFileIndex; i++)
        {
            var path = Path.Combine(folder, BuildFileName(i));
            if (!File.Exists(path)) continue;

            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var msg = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl);
                if (msg == null || msg.Id <= lastSeenMessageId || msg.AuthorId == userId || msg.DeletedAt.HasValue) continue;

                firstUnreadMessageId ??= msg.Id;
                count++;
            }
        }

        return (count, firstUnreadMessageId);
    }

    private async Task<ChatMessage> Enqueue(WriteCommand cmd)
    {
        await _channel.Writer.WriteAsync(cmd);
        return await cmd.Result.Task;
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        await foreach (var cmd in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                var result = cmd.Kind switch
                {
                    WriteKind.Send => await SendAsync(cmd),
                    WriteKind.Edit => await ModifyAsync(cmd, msg => { msg.Text = cmd.Text; msg.EditedAt = DateTime.UtcNow; }),
                    WriteKind.Delete => await ModifyAsync(cmd, msg => msg.DeletedAt = DateTime.UtcNow),
                    WriteKind.Patch => await ModifyAsync(cmd, msg =>
                    {
                        if (cmd.Text != null) { msg.Text = cmd.Text; msg.EditedAt = DateTime.UtcNow; }
                        if (cmd.Attachments != null)
                        {
                            foreach (var att in (msg.Attachments ?? []).Except(cmd.Attachments)) DeleteAttachmentFile(att.Url);
                            msg.Attachments = cmd.Attachments;
                            msg.EditedAt = DateTime.UtcNow;
                        }
                    }),
                    _ => throw new InvalidOperationException("Unknown write kind")
                };
                cmd.Result.SetResult(result);
            }
            catch (Exception ex) { cmd.Result.SetException(ex); }
        }
    }

    private async Task<ChatMessage> SendAsync(WriteCommand cmd)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = await db.Conversations.FindAsync(cmd.ConversationId)
            ?? throw new InvalidOperationException("Conversation not found");

        var folder = GetFolder(conv);
        Directory.CreateDirectory(folder);

        var metaPath = Path.Combine(folder, "dialog.meta.json");
        var dialogMeta = File.Exists(metaPath)
            ? JsonSerializer.Deserialize<DialogMeta>(await File.ReadAllTextAsync(metaPath), s_jsonl)!
            : new DialogMeta();

        if (!_activeFileIndexes.TryGetValue(cmd.ConversationId, out var fileIndex))
        {
            fileIndex = conv.LastClusterId;
            _activeFileIndexes[cmd.ConversationId] = fileIndex;
        }

        var messageId = dialogMeta.LastMessageId + 1;
        var filePath = Path.Combine(folder, BuildFileName(fileIndex));

        if (File.Exists(filePath) && new FileInfo(filePath).Length > 1_048_576)
        {
            fileIndex++;
            _activeFileIndexes[cmd.ConversationId] = fileIndex;
            filePath = Path.Combine(folder, BuildFileName(fileIndex));
        }

        // регистрируем первый id в новом файле
        while (dialogMeta.FileFirstMessageIds.Count <= fileIndex)
            dialogMeta.FileFirstMessageIds.Add(messageId);

        var message = new ChatMessage
        {
            Id = messageId,
            Type = cmd.Type,
            AuthorId = cmd.AuthorId,
            CreatedAt = DateTime.UtcNow,
            Text = cmd.Text,
            Attachments = cmd.Attachments,
            ReplyToMessageId = cmd.ReplyToId
        };

        await File.AppendAllTextAsync(filePath, JsonSerializer.Serialize(message, s_jsonl) + "\n");

        dialogMeta.TotalMessages++;
        dialogMeta.LastMessageId = messageId;
        dialogMeta.LastFileIndex = fileIndex;
        dialogMeta.LastFileSize = new FileInfo(filePath).Length;
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(dialogMeta, s_jsonl));
        _metaCache.Set($"d:{folder}", dialogMeta, MetaOpts);

        // популяция message cache (cache-first для since)
        var list = _msgCache.GetOrCreate(conv.Id, e => { e.SetOptions(MsgOpts); return ImmutableList<ChatMessage>.Empty; }) ?? ImmutableList<ChatMessage>.Empty;
        list = list.Count >= MaxCachedMessages ? list.RemoveAt(0).Add(message) : list.Add(message);
        _msgCache.Set(conv.Id, list, MsgOpts);

        conv.LastMessageTimestamp = message.CreatedAt;
        conv.LastMessageType = cmd.Type;
        conv.LastMessageText = cmd.Text?.Length > 200 ? cmd.Text[..200] : cmd.Text;
        conv.TotalMessagesCount = dialogMeta.TotalMessages;
        conv.LastMessageAuthorId = cmd.AuthorId;
        conv.LastClusterId = fileIndex;

        var recipientId = cmd.AuthorId == conv.SellerId ? conv.BuyerId : conv.SellerId;
        if (recipientId == conv.SellerId) conv.HasUnreadForSeller = true;
        else conv.HasUnreadForBuyer = true;

        // Не изменяем lastSeen для автора при отправке — читаемое/непрочитанное определяется
        // по реальным сообщениям оппонента при подсчёте.
        await db.SaveChangesAsync();

        var author = await db.Users.AsNoTracking()
            .Where(u => u.Id == cmd.AuthorId)
            .Select(u => new { u.UserName, u.UserLogin, u.AvatarPath })
            .FirstOrDefaultAsync();

        // полное сообщение — фронт не делает HTTP-запрос если чат открыт
        var fullPayload = new
        {
            conversationId = conv.Id,
            message.Id, message.Type, message.AuthorId, author, message.CreatedAt,
            message.Text, Attachments = message.Attachments ?? [], message.ReplyToMessageId,
            message.EditedAt, message.DeletedAt
        };

        await hub.Clients.Group($"conversation:{conv.Id}").SendAsync("newMessage", fullPayload);
        // Фоллбэк: отправляем полное сообщение в user:{recipientId}, чтобы получатель получил
        // данные даже если он не вызвал JoinConversation (например чат закрыт).
        await hub.Clients.Group($"user:{recipientId}").SendAsync("newMessage", fullPayload);

        return message;
    }

    private async Task<ChatMessage> ModifyAsync(WriteCommand cmd, Action<ChatMessage> modify)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConversationId)
            ?? throw new InvalidOperationException("Conversation not found");

        var folder = GetFolder(conv);
        var dialogMeta = await LoadDialogMetaAsync(folder)
            ?? throw new InvalidOperationException("Dialog not found");

        var fileIndex = FindFileIndex(dialogMeta, cmd.TargetMessageId!.Value);
        var jsonlPath = Path.Combine(folder, BuildFileName(fileIndex));
        if (!File.Exists(jsonlPath)) throw new InvalidOperationException("Message not found");

        ChatMessage? result = null;
        var tempFile = Path.GetTempFileName();

        try
        {
            using var reader = new StreamReader(jsonlPath);
            using var writer = new StreamWriter(tempFile);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var msg = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl);
                if (msg?.Id == cmd.TargetMessageId)
                {
                    modify(msg);
                    line = JsonSerializer.Serialize(msg, s_jsonl);
                    result = msg;
                }
                await writer.WriteLineAsync(line);
            }
        }
        finally
        {
            File.Delete(jsonlPath);
            File.Move(tempFile, jsonlPath);
        }

        if (result == null) throw new InvalidOperationException("Message not found");

        if (cmd.Kind == WriteKind.Delete)
        {
            var trackedConv = await db.Conversations.FindAsync(cmd.ConversationId)
                ?? throw new InvalidOperationException("Conversation not found");
            var lastMsg = await FindLastNonDeletedMessageAsync(folder, dialogMeta);
            trackedConv.LastMessageText = lastMsg?.Text;
            trackedConv.LastMessageTimestamp = lastMsg?.CreatedAt;
            trackedConv.LastMessageType = lastMsg?.Type;
            trackedConv.LastMessageAuthorId = lastMsg?.AuthorId;
            await db.SaveChangesAsync();
        }

        return result;
    }

    private async Task<UnreadMeta> LoadUnreadMetaAsync(string folder)
    {
        var key = $"u:{folder}";
        if (_metaCache.TryGetValue(key, out UnreadMeta? cached)) return cached!;
        var path = GetUnreadMetaPath(folder);
        var meta = File.Exists(path)
            ? JsonSerializer.Deserialize<UnreadMeta>(await File.ReadAllTextAsync(path), s_jsonl) ?? new UnreadMeta()
            : new UnreadMeta();
        _metaCache.Set(key, meta, MetaOpts);
        return meta;
    }

    private async Task SaveUnreadMetaAsync(string folder, UnreadMeta meta)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(GetUnreadMetaPath(folder), JsonSerializer.Serialize(meta, s_jsonl));
        _metaCache.Set($"u:{folder}", meta, MetaOpts);
    }

    private async Task<DialogMeta?> LoadDialogMetaAsync(string folder)
    {
        var key = $"d:{folder}";
        if (_metaCache.TryGetValue(key, out DialogMeta? cached)) return cached;
        var path = Path.Combine(folder, "dialog.meta.json");
        if (!File.Exists(path)) return null;
        var meta = JsonSerializer.Deserialize<DialogMeta>(await File.ReadAllTextAsync(path), s_jsonl);
        if (meta != null) _metaCache.Set(key, meta, MetaOpts);
        return meta;
    }

    // cache-first для since: O(1) если есть в RAM, фоллбэк на файл
    public (List<ChatMessage> Messages, bool HasMore)? GetCachedMessagesSince(int conversationId, int fromMessageId)
    {
        var list = _msgCache.Get<ImmutableList<ChatMessage>>(conversationId);
        if (list == null || list.Count == 0 || list[0].Id > fromMessageId) return null;
        return (list.Where(m => m.Id >= fromMessageId).ToList(), false);
    }

    private async Task<ChatMessage?> FindLastNonDeletedMessageAsync(string folder, DialogMeta meta)
    {
        for (var i = meta.LastFileIndex; i >= 0; i--)
        {
            var path = Path.Combine(folder, BuildFileName(i));
            if (!File.Exists(path)) continue;
            var lines = await File.ReadAllLinesAsync(path);
            for (var j = lines.Length - 1; j >= 0; j--)
            {
                if (string.IsNullOrWhiteSpace(lines[j])) continue;
                var msg = JsonSerializer.Deserialize<ChatMessage>(lines[j], s_jsonl);
                if (msg != null && !msg.DeletedAt.HasValue) return msg;
            }
        }
        return null;
    }

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
    private static string GetUnreadMetaPath(string folder) => Path.Combine(folder, "dialog.unread.meta.json");

    private string GetFolder(Conversation conv)
    {
        var webRoot = env.WebRootPath ?? "wwwroot";
        return Path.Combine(webRoot, conv.DialogFolderPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    private string GetFolder(string dialogFolderPath)
    {
        var webRoot = env.WebRootPath ?? "wwwroot";
        return Path.Combine(webRoot, dialogFolderPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    private void DeleteAttachmentFile(string relativeUrl)
    {
        var fullPath = Path.Combine(env.WebRootPath ?? "wwwroot", relativeUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    private enum WriteKind { Send, Edit, Delete, Patch }

    private sealed record WriteCommand(
        WriteKind Kind, int ConversationId, int AuthorId, MessageType Type,
        string? Text, int? ReplyToId, int? TargetMessageId,
        List<ChatAttachment>? Attachments,
        TaskCompletionSource<ChatMessage> Result);

    private sealed class UnreadMeta
    {
        public int? SellerLastSeenMessageId { get; set; }
        public int? BuyerLastSeenMessageId { get; set; }
    }
}
