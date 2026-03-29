using System.Text.Json;
using System.Threading.Channels;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Hubs;
using AdsPortalV2.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public class DialogWriterService(IServiceScopeFactory scopeFactory, IWebHostEnvironment env, IHubContext<NotificationHub> hub) : IHostedService
{
    private static readonly JsonSerializerOptions s_jsonl = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly Channel<WriteCommand> _channel = Channel.CreateUnbounded<WriteCommand>();
    private readonly Dictionary<int, int> _activeClusterIds = new();
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
        int conversationId, int authorId, MessageType type, string? text, int? replyToId = null, List<string>? attachments = null)
        => Enqueue(new WriteCommand(WriteKind.Send, conversationId, authorId, type, text, replyToId, null, attachments, new()));

    public Task<ChatMessage> EnqueueEditAsync(int conversationId, int messageId, string? newText)
        => Enqueue(new WriteCommand(WriteKind.Edit, conversationId, 0, default, newText, null, messageId, null, new()));

    public Task<ChatMessage> EnqueueDeleteAsync(int conversationId, int messageId)
        => Enqueue(new WriteCommand(WriteKind.Delete, conversationId, 0, default, null, null, messageId, null, new()));

    public Task<ChatMessage> EnqueuePatchAsync(int conversationId, int messageId, string? text, List<string>? attachments)
        => Enqueue(new WriteCommand(WriteKind.Patch, conversationId, 0, default, text, null, messageId, attachments, new()));

    // Пометка прочитанными только до указанного lastSeenMessageId (обязателен)
    public async Task MarkAsReadAsync(int conversationId, int readerId, int lastSeenMessageId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = await db.Conversations.FindAsync(conversationId)
            ?? throw new InvalidOperationException("Conversation not found");

        var folder = GetFolder(conv);
        if (!Directory.Exists(folder)) return;

        var readAt = DateTime.UtcNow;
        var changed = await MarkAsReadUpToAsync(folder, conv, readerId, lastSeenMessageId, readAt);

        var unreadMeta = await LoadUnreadMetaAsync(conv);

        // always persist last-seen position so frontend can restore viewport
        if (readerId == conv.SellerId)
        {
            unreadMeta.SellerLastSeenMessageId = lastSeenMessageId;
            if (changed > 0)
            {
                unreadMeta.SellerUnreadCount = Math.Max(0, unreadMeta.SellerUnreadCount - changed);
                unreadMeta.SellerFirstUnreadMessageId = await FindFirstUnreadMessageIdAsync(folder, conv, readerId, lastSeenMessageId + 1)
                    ?? (unreadMeta.SellerUnreadCount > 0 ? unreadMeta.SellerFirstUnreadMessageId : null);
            }
        }
        else if (readerId == conv.BuyerId)
        {
            unreadMeta.BuyerLastSeenMessageId = lastSeenMessageId;
            if (changed > 0)
            {
                unreadMeta.BuyerUnreadCount = Math.Max(0, unreadMeta.BuyerUnreadCount - changed);
                unreadMeta.BuyerFirstUnreadMessageId = await FindFirstUnreadMessageIdAsync(folder, conv, readerId, lastSeenMessageId + 1)
                    ?? (unreadMeta.BuyerUnreadCount > 0 ? unreadMeta.BuyerFirstUnreadMessageId : null);
            }
        }

        conv.HasUnreadForSeller = unreadMeta.SellerUnreadCount > 0;
        conv.HasUnreadForBuyer = unreadMeta.BuyerUnreadCount > 0;

        await SaveUnreadMetaAsync(conv, unreadMeta);

        await db.SaveChangesAsync();
    }

    public async Task<(int Count, int? FirstUnreadMessageId)> GetUnreadStateAsync(Conversation conv, int userId)
    {
        var unreadMeta = await LoadUnreadMetaAsync(conv);
        return GetUnreadStateForUser(unreadMeta, conv, userId);
    }

    public async Task<(int Count, int? FirstUnreadMessageId)> GetUnreadStateAsync(
        string dialogFolderPath, int sellerId, int buyerId, int userId)
    {
        var unreadMeta = await LoadUnreadMetaAsync(dialogFolderPath, sellerId, buyerId);
        return GetUnreadStateForUser(unreadMeta, sellerId, buyerId, userId);
    }

    private static bool MarkAsRead(string[] lines, int readerId, DateTime readAt)
    {
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            var msg = JsonSerializer.Deserialize<ChatMessage>(lines[i], s_jsonl);
            if (msg == null || msg.AuthorId == readerId || msg.DeletedAt.HasValue || msg.IsRead) continue;

            msg.IsRead = true;
            msg.ReadAt = readAt;
            lines[i] = JsonSerializer.Serialize(msg, s_jsonl);
            changed = true;
        }

        return changed;
    }

    private static int MarkAsRead(string[] lines, int readerId, int lastSeenMessageId, DateTime readAt)
    {
        var changed = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            var msg = JsonSerializer.Deserialize<ChatMessage>(lines[i], s_jsonl);
            if (msg == null || msg.Id > lastSeenMessageId) break;
            if (msg.AuthorId == readerId || msg.DeletedAt.HasValue || msg.IsRead) continue;

            msg.IsRead = true;
            msg.ReadAt = readAt;
            lines[i] = JsonSerializer.Serialize(msg, s_jsonl);
            changed++;
        }

        return changed;
    }

    private async Task<int> MarkAllAsReadAsync(string folder, int readerId, DateTime readAt)
    {
        var changed = 0;

        foreach (var jsonlPath in Directory.EnumerateFiles(folder, "*.jsonl"))
        {
            var lines = await File.ReadAllLinesAsync(jsonlPath);
            var fileChanged = MarkAsRead(lines, readerId, int.MaxValue, readAt);

            if (fileChanged == 0) continue;

            await File.WriteAllLinesAsync(jsonlPath, lines);
            changed += fileChanged;
        }

        return changed;
    }

    private async Task<int> MarkAsReadUpToAsync(string folder, Conversation conv, int readerId, int lastSeenMessageId, DateTime readAt)
    {
        var changed = 0;
        var targetCluster = await FindClusterIdAsync(folder, lastSeenMessageId, conv);

        for (var clusterId = 0; clusterId <= targetCluster; clusterId++)
        {
            var path = Path.Combine(folder, BuildJsonlName(clusterId, conv));
            if (!File.Exists(path)) continue;

            var lines = await File.ReadAllLinesAsync(path);
            var fileChanged = clusterId == targetCluster
                ? MarkAsRead(lines, readerId, lastSeenMessageId, readAt)
                : MarkAsRead(lines, readerId, int.MaxValue, readAt);

            if (fileChanged == 0) continue;

            await File.WriteAllLinesAsync(path, lines);
            changed += fileChanged;
        }

        return changed;
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
                            foreach (var url in (msg.Attachments ?? []).Except(cmd.Attachments)) DeleteAttachmentFile(url);
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

        // dialog.meta.json
        var dialogMetaPath = Path.Combine(folder, "dialog.meta.json");
        var dialogMeta = File.Exists(dialogMetaPath)
            ? JsonSerializer.Deserialize<DialogMeta>(await File.ReadAllTextAsync(dialogMetaPath), s_jsonl)!
            : new DialogMeta();

        // active_cluster_id — обновляется только здесь, в writer‑task
        if (!_activeClusterIds.TryGetValue(cmd.ConversationId, out var clusterId))
        {
            clusterId = conv.LastClusterId;
            _activeClusterIds[cmd.ConversationId] = clusterId;
        }

        // Проверка лимита 1 МБ → переход к следующему кластеру
        var jsonlPath = Path.Combine(folder, BuildJsonlName(clusterId, conv));
        if (File.Exists(jsonlPath) && new FileInfo(jsonlPath).Length > 1_048_576)
        {
            clusterId++;
            _activeClusterIds[cmd.ConversationId] = clusterId;
            jsonlPath = Path.Combine(folder, BuildJsonlName(clusterId, conv));
        }

        var messageId = dialogMeta.LastMessageId + 1;
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

        // Дозапись в JSONL
        await File.AppendAllTextAsync(jsonlPath, JsonSerializer.Serialize(message, s_jsonl) + "\n");

        // cluster_{id}.meta.json
        var clusterMetaPath = Path.Combine(folder, $"cluster_{clusterId}.meta.json");
        var clusterMeta = File.Exists(clusterMetaPath)
            ? JsonSerializer.Deserialize<ClusterMeta>(await File.ReadAllTextAsync(clusterMetaPath), s_jsonl)!
            : new ClusterMeta { FirstMessageId = messageId };
        clusterMeta.MessageCount++;
        clusterMeta.LastMessageId = messageId;
        await File.WriteAllTextAsync(clusterMetaPath, JsonSerializer.Serialize(clusterMeta, s_jsonl));

        // dialog.meta.json
        dialogMeta.TotalMessages++;
        dialogMeta.LastMessageId = messageId;
        dialogMeta.TotalClusters = clusterId + 1;
        dialogMeta.LastClusterSize = new FileInfo(jsonlPath).Length;
        await File.WriteAllTextAsync(dialogMetaPath, JsonSerializer.Serialize(dialogMeta, s_jsonl));

        // Обновляем Conversation в БД
        conv.LastMessageTimestamp = message.CreatedAt;
        conv.LastMessageType = cmd.Type;
        conv.LastMessageText = cmd.Text?.Length > 200 ? cmd.Text[..200] : cmd.Text;
        conv.TotalMessagesCount = dialogMeta.TotalMessages;
        conv.LastMessageAuthorId = cmd.AuthorId;
        conv.LastClusterId = clusterId;

        var unreadMeta = await LoadUnreadMetaAsync(conv);
        var recipientId = cmd.AuthorId == conv.SellerId ? conv.BuyerId : conv.SellerId;
        if (recipientId == conv.SellerId)
        {
            conv.HasUnreadForSeller = true;
            unreadMeta.SellerUnreadCount++;
            unreadMeta.SellerFirstUnreadMessageId ??= message.Id;
        }
        else
        {
            conv.HasUnreadForBuyer = true;
            unreadMeta.BuyerUnreadCount++;
            unreadMeta.BuyerFirstUnreadMessageId ??= message.Id;
        }

        await SaveUnreadMetaAsync(conv, unreadMeta);

        await db.SaveChangesAsync();

        await hub.Clients.Group($"user:{recipientId}").SendAsync("newMessage", new
        {
            conversationId = conv.Id,
            adId = conv.AdId,
            id = message.Id,
            authorId = cmd.AuthorId,
            text = message.Text,
            attachments = message.Attachments,
            type = cmd.Type,
            replyToMessageId = message.ReplyToMessageId,
            createdAt = message.CreatedAt
        });

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
        var (clusterId, jsonlPath) = await FindClusterPathAsync(folder, cmd.TargetMessageId!.Value, conv);
        if (jsonlPath == null) throw new InvalidOperationException("Message not found");

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
            var lastMsg = await FindLastNonDeletedMessageAsync(folder, conv);
            trackedConv.LastMessageText = lastMsg?.Text;
            trackedConv.LastMessageTimestamp = lastMsg?.CreatedAt;
            trackedConv.LastMessageType = lastMsg?.Type;
            trackedConv.LastMessageAuthorId = lastMsg?.AuthorId;

            var unreadMeta = await RebuildUnreadMetaAsync(conv);
            trackedConv.HasUnreadForSeller = unreadMeta.SellerUnreadCount > 0;
            trackedConv.HasUnreadForBuyer = unreadMeta.BuyerUnreadCount > 0;
            await db.SaveChangesAsync();
        }

        return result;
    }

    private static MessageType DetectMessageType(string ext)
    {
        ext = (ext ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" => MessageType.Image,
            ".mp3" or ".wav" or ".ogg" => MessageType.Audio,
            ".mp4" or ".avi" or ".mov" or ".mkv" => MessageType.Video,
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => MessageType.Document,
            _ => MessageType.File
        };
    }

    private Task<int> GetAudioDurationAsync(string filePath) => Task.FromResult(0);
    private Task<int> GetVideoDurationAsync(string filePath) => Task.FromResult(0);
    private Task<string> GenerateVideoPreviewAsync(string filePath) => Task.FromResult(string.Empty);
    private Task<(int Width, int Height)> GetImageDimensionsAsync(string filePath) => Task.FromResult((0, 0));

    private async Task<UnreadMeta> LoadUnreadMetaAsync(Conversation conv)
    {
        var folder = GetFolder(conv);
        var path = GetUnreadMetaPath(folder);

        if (File.Exists(path))
        {
            var meta = JsonSerializer.Deserialize<UnreadMeta>(await File.ReadAllTextAsync(path), s_jsonl);
            if (meta != null) return meta;
        }

        return await RebuildUnreadMetaAsync(conv);
    }

    private async Task<UnreadMeta> LoadUnreadMetaAsync(string dialogFolderPath, int sellerId, int buyerId)
    {
        var folder = GetFolder(dialogFolderPath);
        var path = GetUnreadMetaPath(folder);

        if (File.Exists(path))
        {
            var meta = JsonSerializer.Deserialize<UnreadMeta>(await File.ReadAllTextAsync(path), s_jsonl);
            if (meta != null) return meta;
        }

        return await RebuildUnreadMetaAsync(folder, sellerId, buyerId);
    }

    private async Task SaveUnreadMetaAsync(Conversation conv, UnreadMeta unreadMeta)
    {
        var folder = GetFolder(conv);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(GetUnreadMetaPath(folder), JsonSerializer.Serialize(unreadMeta, s_jsonl));
    }

    private async Task<UnreadMeta> RebuildUnreadMetaAsync(Conversation conv)
    {
        var folder = GetFolder(conv);
        return await RebuildUnreadMetaAsync(folder, conv.SellerId, conv.BuyerId);
    }

    private async Task<UnreadMeta> RebuildUnreadMetaAsync(string folder, int sellerId, int buyerId)
    {
        var unreadMeta = new UnreadMeta();
        Directory.CreateDirectory(folder);

        var jsonlFiles = Directory.EnumerateFiles(folder, "*.jsonl")
            .OrderBy(GetClusterIdFromJsonlPath)
            .ToList();

        foreach (var jsonlPath in jsonlFiles)
        {
            var lines = await File.ReadAllLinesAsync(jsonlPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var msg = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl);
                if (msg == null || msg.DeletedAt.HasValue || msg.IsRead) continue;

                if (msg.AuthorId == sellerId)
                {
                    unreadMeta.BuyerUnreadCount++;
                    unreadMeta.BuyerFirstUnreadMessageId ??= msg.Id;
                }
                else if (msg.AuthorId == buyerId)
                {
                    unreadMeta.SellerUnreadCount++;
                    unreadMeta.SellerFirstUnreadMessageId ??= msg.Id;
                }
            }
        }

        await File.WriteAllTextAsync(GetUnreadMetaPath(folder), JsonSerializer.Serialize(unreadMeta, s_jsonl));
        return unreadMeta;
    }

    private async Task<int?> FindFirstUnreadMessageIdAsync(string folder, Conversation conv, int readerId, int fromMessageId)
    {
        var startCluster = await FindClusterIdAsync(folder, fromMessageId, conv);

        for (var clusterId = startCluster; clusterId <= conv.LastClusterId; clusterId++)
        {
            var path = Path.Combine(folder, BuildJsonlName(clusterId, conv));
            if (!File.Exists(path)) continue;

            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var msg = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl);
                if (msg == null || msg.Id < fromMessageId) continue;
                if (msg.AuthorId != readerId && !msg.DeletedAt.HasValue && !msg.IsRead) return msg.Id;
            }
        }

        return null;
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

    private async Task<ChatMessage?> FindLastNonDeletedMessageAsync(string folder, Conversation conv)
    {
        for (var clusterId = conv.LastClusterId; clusterId >= 0; clusterId--)
        {
            var path = Path.Combine(folder, BuildJsonlName(clusterId, conv));
            if (!File.Exists(path)) continue;

            var lines = await File.ReadAllLinesAsync(path);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var msg = JsonSerializer.Deserialize<ChatMessage>(lines[i], s_jsonl);
                if (msg != null && !msg.DeletedAt.HasValue) return msg;
            }
        }

        return null;
    }

    private async Task<(int clusterId, string? jsonlPath)> FindClusterPathAsync(string folder, int messageId, Conversation conv)
    {
        var clusterId = await FindClusterIdAsync(folder, messageId, conv);
        var jsonlPath = Path.Combine(folder, BuildJsonlName(clusterId, conv));
        return (clusterId, File.Exists(jsonlPath) ? jsonlPath : null);
    }

    private static (int Count, int? FirstUnreadMessageId) GetUnreadStateForUser(UnreadMeta unreadMeta, Conversation conv, int userId) =>
        userId == conv.SellerId
            ? (unreadMeta.SellerUnreadCount, unreadMeta.SellerFirstUnreadMessageId)
            : userId == conv.BuyerId
                ? (unreadMeta.BuyerUnreadCount, unreadMeta.BuyerFirstUnreadMessageId)
                : (0, null);

    private static (int Count, int? FirstUnreadMessageId) GetUnreadStateForUser(UnreadMeta unreadMeta, int sellerId, int buyerId, int userId) =>
        userId == sellerId
            ? (unreadMeta.SellerUnreadCount, unreadMeta.SellerFirstUnreadMessageId)
            : userId == buyerId
                ? (unreadMeta.BuyerUnreadCount, unreadMeta.BuyerFirstUnreadMessageId)
                : (0, null);

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

    private static int GetClusterIdFromJsonlPath(string path)
    {
        var name = Path.GetFileName(path);
        var index = name.IndexOf('_');
        return index > 0 && int.TryParse(name[..index], out var clusterId) ? clusterId : int.MaxValue;
    }

     private static string BuildJsonlName(int clusterId, Conversation conv) =>
         $"{clusterId}_{conv.AdId}_{conv.SellerId}_{conv.BuyerId}.jsonl";

    private void DeleteAttachmentFile(string relativeUrl)
    {
        var fullPath = Path.Combine(env.WebRootPath ?? "wwwroot", relativeUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    private enum WriteKind { Send, Edit, Delete, Patch }

    private sealed record WriteCommand(
        WriteKind Kind, int ConversationId, int AuthorId, MessageType Type,
        string? Text, int? ReplyToId, int? TargetMessageId,
        List<string>? Attachments,
        TaskCompletionSource<ChatMessage> Result);

    private async Task<object> ExtractMetadataAsync(string filePath, MessageType type)
    {
        var fileInfo = new FileInfo(filePath);
        return type switch
        {
            MessageType.Audio => new { Duration = await GetAudioDurationAsync(filePath), fileInfo.Name },
            MessageType.Video => new { Duration = await GetVideoDurationAsync(filePath), Preview = await GenerateVideoPreviewAsync(filePath), fileInfo.Name },
            MessageType.Document => new { fileInfo.Name, fileInfo.Length },
            MessageType.Image => new { Dimensions = await GetImageDimensionsAsync(filePath), fileInfo.Name },
            _ => new { fileInfo.Name, fileInfo.Length }
        };
    }

    // Возвращает список метаданных для переданных URL-ов вложений в контексте диалога
    public async Task<List<object>?> GetAttachmentsMetadataAsync(Conversation conv, List<string>? attachments)
    {
        if (attachments == null || attachments.Count == 0) return null;
        var folder = GetFolder(conv);
        var result = new List<object>(attachments.Count);

        foreach (var url in attachments)
        {
            var fileName = Path.GetFileName(url);
            var fullPath = Path.Combine(folder, "attachments", fileName);
            if (!File.Exists(fullPath))
            {
                result.Add(new { url, missing = true });
                continue;
            }

            var type = DetectMessageType(Path.GetExtension(fullPath));
            var meta = await ExtractMetadataAsync(fullPath, type);
            result.Add(new { url, type, meta });
        }

        return result;
    }

    private sealed class UnreadMeta
    {
        public int SellerUnreadCount { get; set; }
        public int BuyerUnreadCount { get; set; }
        public int? SellerFirstUnreadMessageId { get; set; }
        public int? BuyerFirstUnreadMessageId { get; set; }
        public int? SellerLastSeenMessageId { get; set; }
        public int? BuyerLastSeenMessageId { get; set; }
    }

    public async Task<int?> GetLastSeenMessageIdAsync(Conversation conv, int userId)
    {
        var meta = await LoadUnreadMetaAsync(conv);
        return userId == conv.SellerId ? meta.SellerLastSeenMessageId : meta.BuyerLastSeenMessageId;
    }
}
