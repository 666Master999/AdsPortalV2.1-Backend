using System.Collections.Immutable;
using System.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Hubs;
using AdsPortalV2.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static AdsPortalV2.Services.DialogHelpers;

namespace AdsPortalV2.Services;

public class DialogWriterService(IServiceScopeFactory scopeFactory, IWebHostEnvironment env, IHubContext<SystemNotificationHub> hub, IHubContext<ChatHub> onlineHub, ILogger<DialogWriterService> logger) : IHostedService
{
    private readonly Channel<WriteCommand> _channel = Channel.CreateBounded<WriteCommand>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Dictionary<int, int> _activeFileIndexes = [];
    // LRU: max 4000 metadata entries (unread + dialog meta), max 1000 message caches
    private readonly MemoryCache _metaCache = new(new MemoryCacheOptions { SizeLimit = 4_000 });
    private readonly MemoryCache _msgCache = new(new MemoryCacheOptions { SizeLimit = 1_000 });
    private const int MaxCachedMessages = 100;
    private static MemoryCacheEntryOptions MetaOpts => new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(30)).SetSize(1);
    private static MemoryCacheEntryOptions MsgOpts => new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(60)).SetSize(1);
    private Task? _writerTask;
    // Buffered writers to reduce per-message open/close I/O
    private readonly Dictionary<int, WriterInfo> _openWriters = new();
    private const int FlushCountThreshold = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WriterIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EvictionInterval = TimeSpan.FromSeconds(10);
    private DateTime _lastWriterEvictionRun = DateTime.MinValue;
    // Dirty set for lightweight reconciliation
    private readonly ConcurrentDictionary<int, byte> _dirtyConversations = new();
    // per-folder semaphore to avoid concurrent writes to dialog.meta.json
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    // Cache of last N messages per conversation to speed up reads
    private readonly ConcurrentDictionary<int, ImmutableList<ChatMessage>> _lastMessagesCache = new();
    private readonly object _lastMessagesCacheLock = new();
    private const int LastMessagesCacheSize = 100;
    private long _lastMessagesCacheHits;
    private long _lastMessagesCacheMisses;
    public int LastMessagesCacheCapacity => LastMessagesCacheSize;
    // Metrics
    private long _messagesSinceLastSample;
    private long _flushesSinceLastSample;
    private double _messagesPerSecond;
    private double _flushesPerSecond;
    private double _averageFileSize;
    private Task? _metricsTask;
    private CancellationTokenSource? _metricsCts;
    private Task? _reconcileTask;
    private CancellationTokenSource? _reconcileCts;
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(1);
    private long _reconcileRuns;
    private long _reconcileFixes;
    private long _reconcileErrors;
    private readonly SemaphoreSlim _recoveryLock = new(1, 1);

    public async Task StartAsync(CancellationToken ct)
    {
        await RecoverPendingWalsAsync(ct);
        _writerTask = Task.Run(() => ProcessAsync(ct), ct);

        // Register snapshot background worker in DI separately; background service will call RunSnapshotPassAsync

        // start metrics sampler
        _metricsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // start reconciliation task
        _reconcileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reconcileTask = Task.Run(() => ReconcileLoopAsync(_reconcileCts.Token));
    }

    private static SemaphoreSlim GetLock(string key) =>
        _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private async Task RecoverPendingWalsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dialogs = await db.Conversations
                .AsNoTracking()
                .Select(c => new { c.Id, c.DialogFolderPath })
                .ToListAsync(ct);

            foreach (var dialog in dialogs)
            {
                ct.ThrowIfCancellationRequested();
                var folder = GetFolder(dialog.DialogFolderPath);
                await ReplayWalAsync(folder, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WAL recovery failed");
        }
    }
    
    public object GetMetrics() => new
    {
        messagesPerSecond = _messagesPerSecond,
        flushesPerSecond = _flushesPerSecond,
        averageFileSize = _averageFileSize
        , reconcileRuns = _reconcileRuns
        , reconcileFixes = _reconcileFixes
        , reconcileErrors = _reconcileErrors
        , dirtyConversations = _dirtyConversations.Count
        , lastMessagesCacheSize = _lastMessagesCache.Count
        , lastMessagesCacheHits = _lastMessagesCacheHits
        , lastMessagesCacheMisses = _lastMessagesCacheMisses
    };

    // Called by SnapshotBackgroundService periodically to perform delta snapshot pass
    public async Task RunSnapshotPassAsync(int threshold, int snapshotLimit)
    {
        try
        {
            var convIds = _dirtyConversations.Keys.ToList();
            foreach (var convId in convIds)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var conv = await db.Conversations.AsNoTracking().Where(c => c.Id == convId).Select(c => new { c.Id, c.DialogFolderPath }).FirstOrDefaultAsync();
                    if (conv == null) { _dirtyConversations.TryRemove(convId, out _); continue; }

                    var folder = GetFolder(conv.DialogFolderPath);
                    var meta = await LoadDialogMetaAsync(folder);
                    if (meta == null) { _dirtyConversations.TryRemove(convId, out _); continue; }

                    // If not enough messages accumulated, skip
                    if (meta.LastMessageId % threshold != 0) continue;

                    await BuildSnapshotAsync(folder, snapshotLimit);
                    _dirtyConversations.TryRemove(convId, out _);
                }
                catch { /* best-effort per conversation */ }
            }
        }
        catch { /* best-effort */ }
    }

    public async Task BuildSnapshotAsync(string folder, int snapshotLimit)
    {
        var sw = Stopwatch.StartNew();
        var meta = await LoadDialogMetaAsync(folder);
        var snapshot = await DialogHelpers.LoadSnapshotAsync(folder, meta);
        if (snapshot == null)
        {
            snapshot = new DialogSnapshot { Messages = new List<ChatMessage>(), LastMessageId = 0, LastFileIndex = 0 };
        }

        var dict = snapshot.Messages.ToDictionary(m => m.Id);
        await ApplyTailAsync(folder, dict, snapshot.LastMessageId);

        var trimmed = dict.Values.OrderBy(x => x.Id).TakeLast(snapshotLimit).ToList();
        var newSnapshot = new DialogSnapshot
        {
            Messages = trimmed,
            LastMessageId = trimmed.Count > 0 ? trimmed.Max(x => x.Id) : snapshot.LastMessageId,
            LastFileIndex = meta?.LastFileIndex ?? 0
        };

        await DialogHelpers.SaveSnapshotAsync(folder, newSnapshot);
        sw.Stop();
        logger.LogInformation("snapshot built for {Folder} in {ElapsedMs} ms with {Count} messages", folder, sw.ElapsedMilliseconds, trimmed.Count);
    }

    private async Task ApplyTailAsync(string folder, Dictionary<int, ChatMessage> dict, int lastId)
    {
        await foreach (var entry in ReadEventsFromId(folder, lastId + 1))
        {
            switch (entry.EventType)
            {
                case DialogMessageEventType.Created:
                    dict[entry.Id] = entry;
                    break;
                case DialogMessageEventType.Edited:
                case DialogMessageEventType.Patched:
                    if (dict.TryGetValue(entry.Id, out var msg))
                    {
                        if (entry.Text != null) msg.Text = entry.Text;
                        if (entry.Attachments != null) msg.Attachments = [.. entry.Attachments];
                        msg.EditedAt = entry.EditedAt ?? DateTime.UtcNow;
                    }
                    break;
                case DialogMessageEventType.Deleted:
                    dict.Remove(entry.Id);
                    break;
            }
        }
    }

    private async IAsyncEnumerable<ChatMessage> ReadEventsFromId(string folder, int fromId)
    {
        var meta = await LoadDialogMetaAsync(folder);
        if (meta == null) yield break;
        var startFile = FindFileIndex(meta, fromId);
        for (var i = startFile; i <= meta.LastFileIndex; i++)
        {
            var path = ResolveFilePath(folder, i);
            if (!File.Exists(path)) continue;
            using var fs = File.OpenRead(path);
            using var reader = new StreamReader(fs);
                while (true)
            {
                var line = await reader.ReadLineAsync();
                    if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                ChatMessage? entry = null;
                try { entry = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl); } catch { continue; }
                if (entry == null) continue;
                if (entry.Id < fromId) continue;
                yield return entry;
            }
        }
    }

    // Fast-path cache accessors for reader
    public (List<ChatMessage> Messages, bool HasMore)? GetLastMessagesFromCache(int conversationId, int count, int? beforeId = null)
    {
        if (!_lastMessagesCache.TryGetValue(conversationId, out var list) || list == null || list.Count == 0)
        {
            Interlocked.Increment(ref _lastMessagesCacheMisses);
            return null;
        }

        // list is ordered ascending by Id
        if (beforeId == null)
        {
            var take = Math.Min(count, list.Count);
            var res = list.Skip(list.Count - take).ToList();
            Interlocked.Increment(ref _lastMessagesCacheHits);
            return (res, list.Count > count);
        }

        if (beforeId <= list[0].Id)
        {
            Interlocked.Increment(ref _lastMessagesCacheMisses);
            return null;
        }

        var filtered = list.Where(m => m.Id < beforeId).ToList();
        if (filtered.Count == 0)
        {
            Interlocked.Increment(ref _lastMessagesCacheMisses);
            return null;
        }

        var take2 = Math.Min(count, filtered.Count);
        var res2 = filtered.Skip(filtered.Count - take2).ToList();
        Interlocked.Increment(ref _lastMessagesCacheHits);
        return (res2, filtered.Count > take2);
    }

    public (List<ChatMessage> Messages, bool HasMore)? GetMessagesSinceFromCache(int conversationId, int fromMessageId)
    {
        if (!_lastMessagesCache.TryGetValue(conversationId, out var list) || list == null || list.Count == 0)
        {
            Interlocked.Increment(ref _lastMessagesCacheMisses);
            return null;
        }

        if (fromMessageId < list[0].Id || fromMessageId > list[^1].Id)
        {
            Interlocked.Increment(ref _lastMessagesCacheMisses);
            return null;
        }

        var res = list.Where(m => m.Id >= fromMessageId).ToList();
        Interlocked.Increment(ref _lastMessagesCacheHits);
        return (res, false);
    }

    // Populate last messages cache (cold-read population)
    public void PopulateLastMessagesCache(int conversationId, List<ChatMessage> messages)
    {
        if (messages == null || messages.Count == 0) return;
        var incoming = BuildLastMessagesCache(messages);
        // Thread-safe overwrite
        lock (_lastMessagesCacheLock)
        {
            _lastMessagesCache[conversationId] = incoming;
        }
    }

    private static ImmutableList<ChatMessage> BuildLastMessagesCache(List<ChatMessage> messages)
        => messages.OrderBy(m => m.Id)
            .TakeLast(LastMessagesCacheSize)
            .ToImmutableList();

    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ReconcileInterval, ct);
                Interlocked.Increment(ref _reconcileRuns);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var dirty = _dirtyConversations.Keys.ToList();
                    foreach (var convId in dirty)
                    {
                        try
                        {
                            var conv = await db.Conversations.AsNoTracking().Where(c => c.Id == convId).Select(c => new { c.Id, c.DialogFolderPath, c.LastMessageTimestamp, c.TotalMessagesCount, c.LastMessageAuthorId, c.LastClusterId }).FirstOrDefaultAsync(ct);
                            if (conv == null)
                            {
                                _dirtyConversations.TryRemove(convId, out _);
                                continue;
                            }
                            var folder = GetFolder(conv.DialogFolderPath);
                            var meta = await LoadDialogMetaAsync(folder);
                            if (meta == null)
                            {
                                _dirtyConversations.TryRemove(convId, out _);
                                continue;
                            }
                            // If dialog.meta.LastMessageId differs from DB TotalMessagesCount/LastClusterId, fix DB
                            if (meta.LastMessageId != conv.TotalMessagesCount || meta.LastFileIndex != conv.LastClusterId)
                            {
                                var tracked = await db.Conversations.FindAsync(conv.Id);
                                if (tracked != null)
                                {
                                    tracked.TotalMessagesCount = meta.LastMessageId;
                                    tracked.LastClusterId = meta.LastFileIndex;
                                    // optional: update LastMessageTimestamp by reading last message
                                    var last = await FindLastNonDeletedMessageAsync(folder, meta);
                                    tracked.LastMessageTimestamp = last?.CreatedAt;
                                    tracked.LastMessageAuthorId = last?.AuthorId;
                                    await db.SaveChangesAsync(ct);
                                    Interlocked.Increment(ref _reconcileFixes);
                                }
                            }

                            // cleaned
                            _dirtyConversations.TryRemove(convId, out _);
                        }
                        catch (Exception)
                        {
                            Interlocked.Increment(ref _reconcileErrors);
                        }
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _reconcileErrors);
                }
            }
        }
        catch (TaskCanceledException) { }
        catch { /* best-effort */ }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _channel.Writer.Complete();
        if (_writerTask != null) await _writerTask;

        // flush and dispose any open writers
        foreach (var kv in _openWriters.Values)
        {
            try { kv.Writer.Flush(); } catch { }
            try { Interlocked.Increment(ref _flushesSinceLastSample); } catch { }
            try { kv.Writer.Dispose(); } catch { }
            try { kv.Fs.Dispose(); } catch { }
        }
        _openWriters.Clear();

        if (_metricsCts != null)
        {
            try { _metricsCts.Cancel(); } catch { }
            try { if (_metricsTask != null) await _metricsTask; } catch { }
            try { _metricsCts.Dispose(); } catch { }
            _metricsCts = null;
        }
        if (_reconcileCts != null)
        {
            try { _reconcileCts.Cancel(); } catch { }
            try { if (_reconcileTask != null) await _reconcileTask; } catch { }
            try { _reconcileCts.Dispose(); } catch { }
            _reconcileCts = null;
        }
    }

    public Task<ChatMessage> EnqueueAsync(
        int conversationId, int authorId, MessageType type, string? text,
        int? replyToId = null, List<ChatAttachment>? attachments = null, string? clientTag = null)
        => Enqueue(new WriteCommand(WriteKind.Send, conversationId, authorId, type, text, replyToId, null, attachments, clientTag, new()));

    public Task<ChatMessage> EnqueueEditAsync(int conversationId, int messageId, string? newText)
        => Enqueue(new WriteCommand(WriteKind.Edit, conversationId, 0, default, newText, null, messageId, null, null, new()));

    public Task<ChatMessage> EnqueueDeleteAsync(int conversationId, int messageId)
        => Enqueue(new WriteCommand(WriteKind.Delete, conversationId, 0, default, null, null, messageId, null, null, new()));

    public Task<ChatMessage> EnqueuePatchAsync(int conversationId, int messageId, string? text, List<ChatAttachment>? attachments)
        => Enqueue(new WriteCommand(WriteKind.Patch, conversationId, 0, default, text, null, messageId, attachments, null, new()));

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

        object[] participants = [
            new { Id = conv.SellerId, LastReadMessageId = unreadMeta.SellerLastSeenMessageId },
            new { Id = conv.BuyerId, LastReadMessageId = unreadMeta.BuyerLastSeenMessageId }
        ];

        await onlineHub.Clients.Group($"conversation:{conversationId}").SendAsync(HubEvents.Read, new
        {
            conversationId,
            userId,
            lastSeenMessageId,
            participants
        });

        // Notify the sender explicitly
        await onlineHub.Clients.User(userId.ToString()).SendAsync(HubEvents.Read, new
        {
            conversationId,
            userId,
            lastSeenMessageId
        });
    }

    public async Task ConfirmReceivedAsync(int conversationId, int userId, int lastReceivedMessageId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = await db.Conversations.FindAsync(conversationId)
            ?? throw new InvalidOperationException("Conversation not found");

        if (userId != conv.SellerId && userId != conv.BuyerId) return;

        var folder = GetFolder(conv);
        var meta = await LoadDeliveryMetaAsync(folder);
        if (userId == conv.SellerId) meta.SellerLastReceivedMessageId = Math.Max(meta.SellerLastReceivedMessageId ?? 0, lastReceivedMessageId);
        else meta.BuyerLastReceivedMessageId = Math.Max(meta.BuyerLastReceivedMessageId ?? 0, lastReceivedMessageId);

        await SaveDeliveryMetaAsync(folder, meta);
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

    private static async Task<(int Count, int? FirstUnreadMessageId)> GetUnreadStateAsync(string folder, DialogMeta dialogMeta, int lastSeenMessageId, int userId)
    {
        if (dialogMeta.LastMessageId <= lastSeenMessageId) return (0, null);

        var msgs = await ToListAsync(ReadMaterializedMessagesAsync(folder, dialogMeta, lastSeenMessageId + 1));
        if (msgs.Count == 0) return (0, null);

        var unread = msgs.Where(m => m.Id > lastSeenMessageId && m.AuthorId != userId && !m.DeletedAt.HasValue).OrderBy(m => m.Id).ToList();
        return (unread.Count, unread.Count > 0 ? unread[0].Id : null);
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

        if (dialogMeta.TotalMessages >= MaxMessagesPerDialog)
            throw new InvalidOperationException("Dialog message limit exceeded");

        var messageId = dialogMeta.LastMessageId + 1;
        var filePath = Path.Combine(folder, BuildFileName(fileIndex));

        if (_openWriters.TryGetValue(cmd.ConversationId, out var existing) && existing.FileIndex == fileIndex)
        {
            try { await existing.Writer.FlushAsync(); } catch { }
            try { existing.Fs.Flush(true); } catch { }
            if (existing.Fs.Length >= MaxFileSizeBytes)
            {
                fileIndex++;
                _activeFileIndexes[cmd.ConversationId] = fileIndex;
                filePath = Path.Combine(folder, BuildFileName(fileIndex));
                try { existing.Writer.Dispose(); } catch { }
                try { existing.Fs.Dispose(); } catch { }
                _openWriters.Remove(cmd.ConversationId);
            }
        }
        else if (File.Exists(filePath) && new FileInfo(filePath).Length >= MaxFileSizeBytes)
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
            ,ClientTag = cmd.ClientTag
        };

        var line = JsonSerializer.Serialize(message, s_jsonl);
        await AppendWalAsync(folder, line);

        var writerInfo = EnsureWriter(cmd.ConversationId, filePath, fileIndex);
        try { await writerInfo.Writer.WriteLineAsync(line); } catch { }
        writerInfo.FileIndex = fileIndex;
        writerInfo.WriteCount++;
        writerInfo.LastWrite = DateTime.UtcNow;

        var nowLocal = DateTime.UtcNow;
        if (writerInfo.WriteCount >= FlushCountThreshold || nowLocal - writerInfo.LastFlush >= FlushInterval || writerInfo.Fs.Length >= MaxFileSizeBytes)
        {
            try { await writerInfo.Writer.FlushAsync(); writerInfo.Fs.Flush(true); Interlocked.Increment(ref _flushesSinceLastSample); writerInfo.LastFlush = nowLocal; writerInfo.WriteCount = 0; } catch { }
            try { dialogMeta.LastFileSize = writerInfo.Fs.Length; } catch { }
            if (dialogMeta.LastFileSize >= MaxFileSizeBytes)
            {
                _activeFileIndexes[cmd.ConversationId] = fileIndex + 1;
            }
        }

        Interlocked.Increment(ref _messagesSinceLastSample);

        // occasional eviction of idle writers to avoid exhausting file descriptors
        var now = DateTime.UtcNow;
        if (now - _lastWriterEvictionRun >= EvictionInterval)
        {
            _lastWriterEvictionRun = now;
            EvictIdleWriters(WriterIdleTimeout);
        }

        // mark conversation dirty for reconciliation
        _dirtyConversations.TryAdd(conv.Id, 0);

        // update last messages cache — store final messages only (simple append, truncation)
        _lastMessagesCache.AddOrUpdate(conv.Id,
            ImmutableList.Create(message),
            (_, list) => list.Count >= LastMessagesCacheSize ? list.RemoveAt(0).Add(message) : list.Add(message));

        dialogMeta.TotalMessages++;
        dialogMeta.LastMessageId = messageId;
        dialogMeta.LastFileIndex = fileIndex;
        dialogMeta.LastFileSize = new FileInfo(filePath).Length;
        // write dialog.meta.json atomically (protected by per-folder semaphore to avoid races)
        var sem = GetLock(folder);
        await sem.WaitAsync();
        try
        {
            var metaJson = JsonSerializer.Serialize(dialogMeta, s_jsonl);
            var tmpMeta = metaPath + ".tmp";
            await File.WriteAllTextAsync(tmpMeta, metaJson);
            File.Move(tmpMeta, metaPath, true);
        }
        finally
        {
            sem.Release();
        }
        await ClearWalAsync(folder);
        // Snapshot trigger is done by background worker now. Do not snapshot here.
        _metaCache.Set($"d:{folder}", dialogMeta, MetaOpts);

        // популяция message cache (cache-first для since)
        var list = _msgCache.GetOrCreate(conv.Id, e => { e.SetOptions(MsgOpts); return ImmutableList<ChatMessage>.Empty; }) ?? [];
        list = list.Count >= MaxCachedMessages ? list.RemoveAt(0).Add(message) : list.Add(message);
        _msgCache.Set(conv.Id, list, MsgOpts);

        conv.LastMessageTimestamp = message.CreatedAt;
        conv.LastMessageType = cmd.Type;
        conv.LastMessageText = cmd.Text?.Length > 200 ? cmd.Text[..200] : cmd.Text;
        conv.TotalMessagesCount = dialogMeta.TotalMessages;
        conv.LastMessageAuthorId = cmd.AuthorId;
        conv.LastClusterId = fileIndex;

        var recipientId = cmd.AuthorId == conv.SellerId ? conv.BuyerId : conv.SellerId;

        // Final safeguard: ensure sender still allowed to send to recipient (race-condition protection)
        var blockService = scope.ServiceProvider.GetRequiredService<IBlockService>();
        if (!await blockService.CanSendAsync(cmd.AuthorId, recipientId))
        {
            // Do not persist delivery or send realtime notifications
            // Rollback any DB changes for this conversation to keep consistency
            // Note: dialog events already appended to WAL and jsonl; best-effort remove last WAL entry
            throw new MessageRejectedException("blocked", "User is blocked");
        }

        if (recipientId == conv.SellerId) conv.HasUnreadForSeller = true;
        else conv.HasUnreadForBuyer = true;



        // Не изменяем lastSeen для автора при отправке — читаемое/непрочитанное определяется
        // по реальным сообщениям оппонента при подсчёте.
        await db.SaveChangesAsync();

        var author = await db.Users.AsNoTracking()
            .Where(u => u.Id == cmd.AuthorId)
            .Select(u => new { u.Id, u.UserName, u.UserLogin, u.AvatarPath })
            .FirstOrDefaultAsync();

        // единый realtime payload
        var fullPayload = new
        {
            conversationId = conv.Id,
            message = new
            {
                conversationId = conv.Id,
                message.Id,
                message.Type,
                message.AuthorId,
                author,
                message.CreatedAt,
                message.Text,
                Attachments = message.Attachments ?? [],
                message.ReplyToMessageId,
                message.EditedAt,
                message.DeletedAt
            }
        };

        // enforce contract: conversationId must be present
        if (fullPayload.conversationId == 0)
            throw new InvalidOperationException("conversationId is required");

        // Send message only to participants who haven't deleted history up to this message
        try
        {
            var participants = new[] { conv.SellerId, conv.BuyerId }.Distinct();
            foreach (var u in participants)
            {
                var marker = u == conv.SellerId ? conv.SellerDeletedUpToMessageId : conv.BuyerDeletedUpToMessageId;
                if (marker == null || message.Id > marker.Value)
                {
                    try { await hub.Clients.Group($"user:{u}").SendAsync(HubEvents.Message, fullPayload); } catch (Exception ex) { logger.LogWarning(ex, "Failed to notify user {UserId} about message", u); }
                    try { await onlineHub.Clients.Group($"user:{u}").SendAsync(HubEvents.Message, fullPayload); } catch (Exception ex) { logger.LogWarning(ex, "Failed to notify online user {UserId} about message", u); }
                }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Per-recipient send failed"); }

        var fullConversation = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conv.Id)
            .Include(c => c.Ad).ThenInclude(a => a.Images)
            .Include(c => c.Seller)
            .Include(c => c.Buyer)
            .FirstAsync();

        var (Count, FirstUnreadMessageId) = await GetUnreadStateAsync(fullConversation, fullConversation.SellerId);
        var buyerUnread = await GetUnreadStateAsync(fullConversation, fullConversation.BuyerId);

        var sellerDto = fullConversation.ToDto(fullConversation.SellerId, Count, FirstUnreadMessageId, message.Id);
        var buyerDto = fullConversation.ToDto(fullConversation.BuyerId, buyerUnread.Count, buyerUnread.FirstUnreadMessageId, message.Id);

        await hub.Clients.Group($"user:{conv.SellerId}").SendAsync(HubEvents.ConversationUpdated, sellerDto);
        await onlineHub.Clients.Group($"user:{conv.SellerId}").SendAsync(HubEvents.ConversationUpdated, sellerDto);
        if (conv.BuyerId != conv.SellerId)
        {
            await hub.Clients.Group($"user:{conv.BuyerId}").SendAsync(HubEvents.ConversationUpdated, buyerDto);
            await onlineHub.Clients.Group($"user:{conv.BuyerId}").SendAsync(HubEvents.ConversationUpdated, buyerDto);
        }

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

        // Close any open writer for this conversation to avoid conflicting appends
        if (_openWriters.TryGetValue(cmd.ConversationId, out var open))
        {
            try { open.Writer.Flush(); } catch { }
            try { open.Writer.Dispose(); } catch { }
            try { open.Fs.Dispose(); } catch { }
            _openWriters.Remove(cmd.ConversationId);
        }

        var messages = await ToListAsync(ReadMaterializedMessagesAsync(folder, dialogMeta, cmd.TargetMessageId!.Value));
        var current = messages.FirstOrDefault(m => m.Id == cmd.TargetMessageId!.Value)
            ?? throw new InvalidOperationException("Message not found");

        var result = new ChatMessage
        {
            Id = current.Id,
            EventType = cmd.Kind == WriteKind.Delete ? DialogMessageEventType.Deleted : cmd.Kind == WriteKind.Edit ? DialogMessageEventType.Edited : DialogMessageEventType.Patched,
            Type = current.Type,
            AuthorId = current.AuthorId,
            CreatedAt = current.CreatedAt,
            Text = current.Text,
            Attachments = current.Attachments != null ? [.. current.Attachments] : [],
            ReplyToMessageId = current.ReplyToMessageId,
            EditedAt = DateTime.UtcNow,
            DeletedAt = cmd.Kind == WriteKind.Delete ? DateTime.UtcNow : null
        };

        modify(result);

        await AppendWalAsync(folder, JsonSerializer.Serialize(result, s_jsonl));
        await AppendEventAsync(jsonlPath, result);
        dialogMeta.LastFileSize = new FileInfo(jsonlPath).Length;
        // write dialog.meta.json atomically
        var metaPath2 = Path.Combine(folder, "dialog.meta.json");
        var metaJson2 = JsonSerializer.Serialize(dialogMeta, s_jsonl);
        var tmpMeta2 = metaPath2 + ".tmp";
        await File.WriteAllTextAsync(tmpMeta2, metaJson2);
        File.Move(tmpMeta2, metaPath2, true);
        _metaCache.Set($"d:{folder}", dialogMeta, MetaOpts);

        if (result.EventType == DialogMessageEventType.Deleted)
            result.Text = null;

        // Invalidate in-memory message cache for this conversation to avoid stale data
        _msgCache.Remove(cmd.ConversationId);
        // remove last messages cache so next read will load from files
        _lastMessagesCache.TryRemove(cmd.ConversationId, out _);
        // mark conversation dirty for reconciliation
        _dirtyConversations.TryAdd(cmd.ConversationId, 0);

        if (cmd.Kind == WriteKind.Delete)
        {
            var trackedConv = await db.Conversations.FindAsync(cmd.ConversationId)
                ?? throw new InvalidOperationException("Conversation not found");
            var lastMsg = messages.OrderBy(m => m.Id).LastOrDefault(m => !m.DeletedAt.HasValue);
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

    private async Task<DeliveryMeta> LoadDeliveryMetaAsync(string folder)
    {
        var key = $"r:{folder}";
        if (_metaCache.TryGetValue(key, out DeliveryMeta? cached)) return cached!;
        var path = GetDeliveryMetaPath(folder);
        var meta = File.Exists(path)
            ? JsonSerializer.Deserialize<DeliveryMeta>(await File.ReadAllTextAsync(path), s_jsonl) ?? new DeliveryMeta()
            : new DeliveryMeta();
        _metaCache.Set(key, meta, MetaOpts);
        return meta;
    }

    private async Task SaveUnreadMetaAsync(string folder, UnreadMeta meta)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(GetUnreadMetaPath(folder), JsonSerializer.Serialize(meta, s_jsonl));
        _metaCache.Set($"u:{folder}", meta, MetaOpts);
    }

    private async Task SaveDeliveryMetaAsync(string folder, DeliveryMeta meta)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(GetDeliveryMetaPath(folder), JsonSerializer.Serialize(meta, s_jsonl));
        _metaCache.Set($"r:{folder}", meta, MetaOpts);
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

    private static async Task<ChatMessage?> FindLastNonDeletedMessageAsync(string folder, DialogMeta meta)
    {
        for (var i = meta.LastFileIndex; i >= 0; i--)
        {
            var path = ResolveFilePath(folder, i);
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = await DialogHelpers.OpenReadWithRetryAsync(path, CancellationToken.None);
                using var reader = new StreamReader(stream);
                var buffer = new List<string>();
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    buffer.Add(line);
                }
                for (var j = buffer.Count - 1; j >= 0; j--)
                {
                    var msg = JsonSerializer.Deserialize<ChatMessage>(buffer[j], s_jsonl);
                    if (msg != null && !msg.DeletedAt.HasValue) return msg;
                }
            }
            catch { /* best-effort */ }
        }
        return null;
    }

    private static string GetUnreadMetaPath(string folder) => Path.Combine(folder, "dialog.unread.meta.json");

    private static string GetDeliveryMetaPath(string folder) => Path.Combine(folder, "dialog.delivery.meta.json");

    private string GetFolder(Conversation conv)
    {
        var webRoot = env.WebRootPath ?? "wwwroot";
        // conv.DialogFolderPath expected to be: files/{userId}/Ads/{adId}/dialogs/{conversationId}
        return Path.Combine(webRoot, conv.DialogFolderPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    private string GetFolder(string dialogFolderPath)
    {
        var webRoot = env.WebRootPath ?? "wwwroot";
        return Path.Combine(webRoot, dialogFolderPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    private string GetAttachmentFolder(Conversation conv)
    {
        return Path.Combine(env.WebRootPath ?? "wwwroot", conv.DialogFolderPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
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
        string? ClientTag,
        TaskCompletionSource<ChatMessage> Result);

    private sealed class WriterInfo
    {
        public FileStream Fs { get; set; } = null!;
        public StreamWriter Writer { get; set; } = null!;
        public int FileIndex { get; set; }
        public int WriteCount { get; set; }
        public DateTime LastWrite { get; set; }
        public DateTime LastFlush { get; set; }
    }

    private WriterInfo EnsureWriter(int conversationId, string filePath, int fileIndex)
    {
        if (_openWriters.TryGetValue(conversationId, out var info))
        {
            // if file index changed (rotation), replace writer
            if (info.FileIndex != fileIndex)
            {
                try { info.Writer.Flush(); } catch { }
                try { info.Writer.Dispose(); } catch { }
                try { info.Fs.Dispose(); } catch { }
                _openWriters.Remove(conversationId);
            }
            else return info;
        }

        var dir = Path.GetDirectoryName(filePath) ?? ".";
        Directory.CreateDirectory(dir);
        var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var sw = new StreamWriter(fs) { AutoFlush = false };
        info = new WriterInfo { Fs = fs, Writer = sw, FileIndex = fileIndex, WriteCount = 0, LastWrite = DateTime.UtcNow, LastFlush = DateTime.UtcNow };
        _openWriters[conversationId] = info;
        return info;
    }

    private void EvictIdleWriters(TimeSpan idleTimeout)
    {
        var now = DateTime.UtcNow;
        var toClose = _openWriters.Where(kv => now - kv.Value.LastWrite >= idleTimeout).Select(kv => kv.Key).ToList();
        foreach (var key in toClose)
        {
            if (_openWriters.TryGetValue(key, out var info))
            {
                try { info.Writer.Flush(); } catch { }
                try { info.Writer.Dispose(); } catch { }
                try { info.Fs.Dispose(); } catch { }
                _openWriters.Remove(key);
            }
        }
    }

    private static async Task AppendEventAsync(string path, ChatMessage eventMessage)
    {
        var line = JsonSerializer.Serialize(eventMessage, s_jsonl);
        await AppendLineAsync(path, line);
    }

    private static async Task AppendLineAsync(string path, string line)
    {
        // Append a raw line into a .jsonl file using FileShare.Read so readers can open concurrently
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync(line);
        await writer.FlushAsync();
        try { stream.Flush(true); } catch { }
    }

    private async Task AppendWalAsync(string folder, string line)
    {
        var path = BuildWalPath(folder);
        Directory.CreateDirectory(folder);
        await AppendLineAsync(path, line);
    }

    private async Task ClearWalAsync(string folder)
    {
        var path = BuildWalPath(folder);
        if (!File.Exists(path)) return;
        // Truncate file while allowing readers to open it (FileShare.Read)
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await fs.FlushAsync();
            try { fs.Flush(true); } catch { }
        }
        catch
        {
            // best-effort
        }
    }

    private async Task ReplayWalAsync(string folder, CancellationToken ct)
    {
        var path = BuildWalPath(folder);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;

        await _recoveryLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return;

            var lines = await File.ReadAllLinesAsync(path, ct);
            if (lines.Length == 0) return;

            var meta = await LoadDialogMetaAsync(folder);
            if (meta == null) return;

            var existingIds = (await ToListAsync(ReadMaterializedMessagesAsync(folder, meta))).Select(m => m.Id).ToHashSet();

            var filePath = ResolveFilePath(folder, meta.LastFileIndex);
            Directory.CreateDirectory(folder);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var entry = JsonSerializer.Deserialize<ChatMessage>(line, s_jsonl);
                if (entry != null && existingIds.Contains(entry.Id)) continue;
                await AppendLineAsync(filePath, line);
            }

            await File.WriteAllTextAsync(path, string.Empty, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to replay WAL for {Folder}", folder);
        }
        finally
        {
            _recoveryLock.Release();
        }
    }

    private async Task<long> GetTotalWalSizeAsync()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dialogs = await db.Conversations.AsNoTracking().Select(c => c.DialogFolderPath).ToListAsync();
            long sum = 0;
            foreach (var dialogFolderPath in dialogs)
            {
                var path = BuildWalPath(GetFolder(dialogFolderPath));
                if (File.Exists(path))
                    sum += new FileInfo(path).Length;
            }
            return sum;
        }
        catch
        {
            return 0;
        }
    }

    private sealed class UnreadMeta
    {
        public int? SellerLastSeenMessageId { get; set; }
        public int? BuyerLastSeenMessageId { get; set; }
    }

    private sealed class DeliveryMeta
    {
        public int? SellerLastReceivedMessageId { get; set; }
        public int? BuyerLastReceivedMessageId { get; set; }
    }
}
