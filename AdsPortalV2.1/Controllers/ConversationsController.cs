using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Hubs;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("conversations")]
[Authorize]
public class ConversationsController(
    AppDbContext db,
    DialogWriterService writer,
    DialogReaderService reader,
    ImageService imageService,
    IWebHostEnvironment env,
    OnlineUserTracker tracker,
    IHubContext<OnlineHub> onlineHub,
    IHubContext<NotificationHub> notificationHub,
    PermissionService perms,
    ILogger<ConversationsController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConversationRequest req)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();

        // Comment ban: prevent starting conversations
        if (await perms.HasActiveRestrictionAsync(buyerId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));

        var ad = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.AdId);
        if (ad == null) return NotFound();

        if (await IsBlockedBidirectionalAsync(buyerId, ad.UserId))
            return StatusCode(403, new ApiError("forbidden", "Недостаточно прав для общения с этим пользователем."));

        if (ad.UserId == buyerId)
            return BadRequest(new ApiError("validation_error", "Нельзя написать самому себе."));

        var existing = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AdId == req.AdId && c.BuyerId == buyerId);
        if (existing != null)
        {
            var existingDto = await LoadConversationDtoAsync(existing.Id, buyerId);
            return Ok(new ConversationActionDto(existing.Id, "already_exists", existingDto));
        }

        var conv = new Conversation
        {
            AdId = ad.Id,
            SellerId = ad.UserId,
            BuyerId = buyerId
        };

        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        conv.DialogFolderPath = $"files/{ad.UserId}/Ads/{ad.Id}/dialogs/{conv.Id}";
        await db.SaveChangesAsync();
        Directory.CreateDirectory(Path.Combine(env.WebRootPath ?? "wwwroot", conv.DialogFolderPath));
        Directory.CreateDirectory(Path.Combine(env.WebRootPath ?? "wwwroot", conv.DialogFolderPath, "attachments"));

        var buyerDto = await LoadConversationDtoAsync(conv.Id, buyerId);
        var sellerDto = conv.SellerId == conv.BuyerId ? buyerDto : await LoadConversationDtoAsync(conv.Id, conv.SellerId);

        if (buyerDto == null || sellerDto == null)
            return NotFound();

        // realtime: уведомим участников (buyer и seller) о новом диалоге
        try
        {
            // send to user groups (group-based) as well as to User identifier as fallback
            await notificationHub.Clients.Group($"user:{conv.SellerId}").SendAsync("chat:conversationCreated", sellerDto);
            await onlineHub.Clients.Group($"user:{conv.SellerId}").SendAsync("chat:conversationCreated", sellerDto);
            await notificationHub.Clients.User(conv.SellerId.ToString()).SendAsync("chat:conversationCreated", sellerDto);
            if (conv.BuyerId != conv.SellerId)
            {
                await notificationHub.Clients.Group($"user:{conv.BuyerId}").SendAsync("chat:conversationCreated", buyerDto);
                await onlineHub.Clients.Group($"user:{conv.BuyerId}").SendAsync("chat:conversationCreated", buyerDto);
                await notificationHub.Clients.User(conv.BuyerId.ToString()).SendAsync("chat:conversationCreated", buyerDto);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send chat:conversationCreated realtime notification for conversation {ConvId}", conv.Id);
        }

        return Ok(new ConversationActionDto(conv.Id, "created", buyerDto));
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conversations = await db.Conversations
            .AsNoTracking()
            .Where(c => c.SellerId == userId || c.BuyerId == userId)
            .Include(c => c.Ad).ThenInclude(a => a.Images) // Ensure Ad data is included
            .Include(c => c.Seller) // Ensure Seller data is included
            .Include(c => c.Buyer) // Ensure Buyer data is included
            .OrderByDescending(c => c.LastMessageTimestamp)
            .ToListAsync();

        conversations = [.. conversations.Where(c => !IsBlockedBidirectional(c.SellerId, c.BuyerId))];

        var list = await Task.WhenAll(conversations.Select(async c =>
        {
            var (Count, FirstUnreadMessageId) = await writer.GetUnreadStateAsync(c, userId);
            return c.ToDto(userId, Count, FirstUnreadMessageId);
        }));

        return Ok(list);
    }

    [HttpGet("{id:int}/messages")]
    public async Task<IActionResult> GetMessages(int id, [FromQuery] int count = 10, [FromQuery] int? before = null, [FromQuery] int? since = null)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var isInitialLoad = before == null && since == null;

        var conv = isInitialLoad
            ? await db.Conversations.AsNoTracking()
                .Include(c => c.Ad).ThenInclude(a => a.Images)
                .Include(c => c.Seller).Include(c => c.Buyer)
                .FirstOrDefaultAsync(c => c.Id == id)
            : await db.Conversations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id);

        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId)) return Forbid();
        if (IsBlockedBidirectional(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        List<ChatMessage> messages;
        bool hasMore;
        int? anchorMessageId = null;

        if (since.HasValue)
        {
            var cached = writer.GetCachedMessagesSince(conv.Id, since.Value + 1);
            (messages, hasMore) = cached ?? await reader.GetMessagesSinceAsync(conv, since.Value + 1);
        }
        else if (!isInitialLoad)
            (messages, hasMore) = await reader.GetMessagesAsync(conv, count, before);
        else
        {
            anchorMessageId = await writer.GetLastSeenMessageIdAsync(conv, userId);
            (messages, hasMore) = await reader.GetMessagesAsync(conv, count, anchorMessageId.HasValue ? anchorMessageId.Value + 1 : null);

            // Добавим также непрочитанные/новые сообщения (id >= anchor+1), чтобы фронт получил
            // как последние старые, так и свежие сообщения в одном ответе.
            if (anchorMessageId.HasValue)
            {
                var (newMsgs, _) = await reader.GetMessagesSinceAsync(conv, anchorMessageId.Value + 1);
                if (newMsgs?.Count > 0)
                {
                    var existingIds = messages.Select(m => m.Id).ToHashSet();
                    messages.AddRange(newMsgs.Where(m => !existingIds.Contains(m.Id)));
                }
            }
        }

        var userList = await db.Users
            .Where(u => messages.Select(m => m.AuthorId).Distinct().Contains(u.Id))
            .Select(u => new { u.Id, u.UserName, u.UserLogin, u.AvatarPath })
            .ToListAsync();

        var users = userList.ToDictionary(u => u.Id, u => new MessageAuthorDto(u.UserName, u.UserLogin, u.AvatarPath));

        var enrichedMessages = messages.Select(m =>
        {
            var author = users.GetValueOrDefault(m.AuthorId);
            return new ConversationMessageDto(
                id,
                m.Id,
                m.Type,
                m.AuthorId,
                author == null ? null : new MessageAuthorDto(author.UserName, author.UserLogin, author.AvatarPath),
                m.CreatedAt,
                m.Text,
                m.Attachments ?? [],
                m.ReplyToMessageId,
                m.EditedAt,
                m.DeletedAt);
        }).ToList();

        if (!isInitialLoad)
            return Ok(new ConversationMessagesChunkDto(enrichedMessages, hasMore));

        var opponentId = conv.SellerId == userId ? conv.BuyerId : conv.SellerId;
        var myLastSeenMessageId = anchorMessageId;
        var otherLastSeenMessageId = await writer.GetLastSeenMessageIdAsync(conv, opponentId);

        var me = conv.SellerId == userId ? conv.Seller : conv.Buyer;
        var opponent = conv.SellerId == userId ? conv.Buyer : conv.Seller;

        // при initial load пометим сообщения как прочитанные для текущего пользователя,
        // чтобы счётчик непрочитанных синхронизировался сразу при открытии диалога
        if (isInitialLoad && messages.Count > 0)
        {
            var lastMsgId = messages.Last().Id;
            await writer.MarkAsReadAsync(conv.Id, userId, lastMsgId);
        }

        var conversationMeta = new ConversationMetaDto(
            conv.Id,
            conv.AdId,
            new ConversationAdMetaDto(
                conv.Ad.Id,
                conv.Ad.Title,
                conv.Ad.Price,
                conv.Ad.CreatedAt,
                conv.Ad.Status,
                conv.Ad.Images.FirstOrDefault(img => img.Id == conv.Ad.MainImageId)?.FilePath),
            new ConversationUserPresenceDto(me.Id, me.UserName, me.UserLogin, me.AvatarPath, me.LastActivityAt, tracker.IsOnline(userId)),
            new ConversationUserPresenceDto(opponent.Id, opponent.UserName, opponent.UserLogin, opponent.AvatarPath, opponent.LastActivityAt, tracker.IsOnline(opponentId)),
            conv.CreatedAt,
            conv.IsClosed,
            conv.LastMessageText);

        return Ok(new ConversationInitialDto(conversationMeta, enrichedMessages, hasMore, anchorMessageId, myLastSeenMessageId, otherLastSeenMessageId));
    }

    [HttpPost("{id:int}/messages")]
    [EnableRateLimiting("MessagesPerSecond")]
    [Consumes("application/json")]
    public async Task<IActionResult> SendMessage(int id, [FromBody] SendMessageRequest req)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        // Comment ban: prevent sending messages
        if (await perms.HasActiveRestrictionAsync(userId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId)) return Forbid();
        if (await IsBlockedBidirectionalAsync(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        if (string.IsNullOrWhiteSpace(req.Text) && req.ReplyToMessageId == null && req.Type == MessageType.Text)
            return BadRequest(new ApiError("validation_error", "Empty message body"));

        var msg = await writer.EnqueueAsync(id, userId, req.Type, req.Text, req.ReplyToMessageId);
        return Ok(new ConversationMessageActionDto(id, msg));
    }

    [HttpPost("{id:int}/messages")]
    [EnableRateLimiting("MessagesPerSecond")]
    [Consumes("multipart/form-data")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> SendMessage(int id, [FromForm] string? text, [FromForm] int? replyToMessageId, [FromForm] List<IFormFile>? files)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        // Comment ban: prevent sending messages
        if (await perms.HasActiveRestrictionAsync(userId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId)) return Forbid();
        if (await IsBlockedBidirectionalAsync(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        // Some clients send the text
        var contentText = text;
        if (string.IsNullOrWhiteSpace(contentText) && Request.HasFormContentType)
            contentText = Request.Form["caption"].FirstOrDefault();

        var hasFiles = files is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(contentText) && !hasFiles && replyToMessageId == null)
            return BadRequest(new ApiError("validation_error", "Empty message body"));

        if (!hasFiles)
        {
            var message = await writer.EnqueueAsync(id, userId, MessageType.Text, contentText, replyToMessageId);
            return Ok(new ConversationMessageActionDto(id, message));
        }

        var attachments = await Task.WhenAll(files!.Select(f => SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == nameof(MessageType.Image)) ? MessageType.Image : MessageType.File;
        var msg = await writer.EnqueueAsync(id, userId, type, contentText, replyToMessageId, [.. attachments]);
        return Ok(new ConversationMessageActionDto(id, msg));
    }

    [HttpPost("by-ad/{adId:int}/messages")]
    [EnableRateLimiting("MessagesPerSecond")]
    [Consumes("application/json")]
    public async Task<IActionResult> SendMessageByAd(int adId, [FromBody] SendMessageRequest req)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();
        if (await perms.HasActiveRestrictionAsync(buyerId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));
        var conv = await GetOrCreateByAdAsync(adId, buyerId);
        if (conv == null) return NotFound();
        var msg = await writer.EnqueueAsync(conv.Id, buyerId, req.Type, req.Text, req.ReplyToMessageId);
        return Ok(new ConversationMessageActionDto(conv.Id, msg));
    }

    [HttpPost("by-ad/{adId:int}/messages")]
    [EnableRateLimiting("MessagesPerSecond")]
    [Consumes("multipart/form-data")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> SendMessageByAd(int adId, [FromForm] string? text, [FromForm] int? replyToMessageId, [FromForm] List<IFormFile>? files)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();
        if (await perms.HasActiveRestrictionAsync(buyerId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));
        var conv = await GetOrCreateByAdAsync(adId, buyerId);
        if (conv == null) return NotFound();
        var contentText = string.IsNullOrWhiteSpace(text) ? Request.Form["caption"].FirstOrDefault() : text;
        var hasFiles = files is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(contentText) && !hasFiles && replyToMessageId == null)
            return BadRequest(new ApiError("validation_error", "Empty message body"));
        if (!hasFiles)
            return Ok(new ConversationMessageActionDto(conv.Id, await writer.EnqueueAsync(conv.Id, buyerId, MessageType.Text, contentText, replyToMessageId)));
        var attachments = await Task.WhenAll(files!.Select(f => SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == nameof(MessageType.Image)) ? MessageType.Image : MessageType.File;
        return Ok(new ConversationMessageActionDto(conv.Id, await writer.EnqueueAsync(conv.Id, buyerId, type, contentText, replyToMessageId, [.. attachments])));
    }

    [HttpPatch("{id:int}/read")]
    public async Task<IActionResult> MarkAsRead(int id, [FromQuery] int? lastSeenMessageId)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        if (!lastSeenMessageId.HasValue) return BadRequest(new ApiError("validation_error", "lastSeenMessageId is required"));

        var conv = await db.Conversations.FindAsync(id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId)) return Forbid();
        if (IsBlockedBidirectional(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        await writer.MarkAsReadAsync(id, userId, lastSeenMessageId.Value);
        await onlineHub.Clients.Group($"conversation:{id}").SendAsync("chat:read", new
        {
            conversationId = id,
            userId,
            lastSeenMessageId = lastSeenMessageId.Value
        });
        return Ok();
    }

    [HttpPatch("{id:int}/mute")]
    public async Task<IActionResult> ToggleMute(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.FindAsync(id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId)) return Forbid();
        if (IsBlockedBidirectional(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        if (userId == conv.SellerId) conv.IsMutedForSeller = !conv.IsMutedForSeller;
        else conv.IsMutedForBuyer = !conv.IsMutedForBuyer;

        await db.SaveChangesAsync();
        return Ok();
    }

    [HttpPatch("{id:int}/archive")]
    public async Task<IActionResult> ToggleArchive(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.FindAsync(id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId)) return Forbid();

        if (userId == conv.SellerId) conv.IsArchivedForSeller = !conv.IsArchivedForSeller;
        else conv.IsArchivedForBuyer = !conv.IsArchivedForBuyer;

        await db.SaveChangesAsync();
        return Ok();
    }

    [HttpPatch("{id:int}/messages/{messageId:int}")]
    public async Task<IActionResult> EditMessage(int id, int messageId, [FromBody] EditMessageRequest req)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId))
            return StatusCode(403, new ApiError("forbidden", "Недостаточно прав для доступа к этому диалогу."));
        if (IsBlockedBidirectional(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        var existing = await reader.FindByIdAsync(conv, messageId);
        if (existing == null) return NotFound();
        if (existing.AuthorId != userId)
            return StatusCode(403, new ApiError("forbidden", "Вы не являетесь автором этого сообщения."));

        var updated = await writer.EnqueuePatchAsync(id, messageId, req.Text, req.Attachments);
        return Ok(new ConversationMessageActionDto(id, updated));
    }

    [HttpPost("{id:int}/messages/{messageId:int}/attachments")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> AddMessageAttachment(int id, int messageId, [FromForm] List<IFormFile> files)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId))
            return StatusCode(403, new ApiError("forbidden", "Недостаточно прав для доступа к этому диалогу."));
        if (IsBlockedBidirectional(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        var existing = await reader.FindByIdAsync(conv, messageId);
        if (existing == null) return NotFound();
        if (existing.AuthorId != userId)
            return StatusCode(403, new ApiError("forbidden", "Вы не являетесь автором этого сообщения."));

        var newAttachments = await Task.WhenAll(files.Select(f => SaveAttachmentAsync(conv, f)));
        var combined = (existing.Attachments ?? []).Concat(newAttachments).ToList();
        var updated = await writer.EnqueuePatchAsync(id, messageId, null, combined);
        return Ok(new ConversationMessageActionDto(id, updated));
    }

    [HttpDelete("{id:int}/messages/{messageId:int}")]
    public async Task<IActionResult> DeleteMessage(int id, int messageId)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId)) return Forbid();
        if (IsBlockedBidirectional(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        var existing = await reader.FindByIdAsync(conv, messageId);
        if (existing == null) return NotFound();
        if (existing.AuthorId != userId) return Forbid();

        var updated = await writer.EnqueueDeleteAsync(id, messageId);
        return Ok(new ConversationMessageActionDto(id, updated));
    }

    [HttpPost("{id:int}/attachments")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> UploadAttachment(int id, [FromForm] List<IFormFile> files, [FromForm] string? caption)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId)) return Forbid();
        if (IsBlockedBidirectional(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        var attachments = await Task.WhenAll(files.Select(f => SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == nameof(MessageType.Image)) ? MessageType.Image : MessageType.File;
        var msg = await writer.EnqueueAsync(id, userId, type, caption, attachments: [.. attachments]);
        return Ok(new ConversationMessageActionDto(id, msg));
    }

    [HttpPost("by-ad/{adId:int}/attachments")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> UploadAttachmentByAd(int adId, [FromForm] List<IFormFile> files, [FromForm] string? caption)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();
        var conv = await GetOrCreateByAdAsync(adId, buyerId);
        if (conv == null) return NotFound();
        var attachments = await Task.WhenAll(files.Select(f => SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == nameof(MessageType.Image)) ? MessageType.Image : MessageType.File;
        return Ok(new ConversationMessageActionDto(conv.Id, await writer.EnqueueAsync(conv.Id, buyerId, type, caption, attachments: [.. attachments])));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetConversation(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();
        var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conversation == null) return NotFound();
        if (!conversation.IsParticipant(userId)) return Forbid();
        if (IsBlockedBidirectional(conversation.SellerId, conversation.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));
        var (Count, FirstUnreadMessageId) = await writer.GetUnreadStateAsync(conversation, userId);
        var opponentId = conversation.SellerId == userId ? conversation.BuyerId : conversation.SellerId;
        var myLastSeenMessageId = await writer.GetLastSeenMessageIdAsync(conversation, userId);
        var otherLastSeenMessageId = await writer.GetLastSeenMessageIdAsync(conversation, opponentId);
        return Ok(new ConversationStateDto(
            conversation.Id,
            conversation.AdId,
            conversation.SellerId,
            conversation.BuyerId,
            conversation.CreatedAt,
            conversation.IsClosed,
            conversation.LastMessageTimestamp,
            conversation.LastMessageType,
            conversation.LastMessageText,
            conversation.LastMessageAuthorId,
            conversation.TotalMessagesCount,
            Count > 0,
            Count,
            myLastSeenMessageId,
            otherLastSeenMessageId,
            conversation.SellerId == userId ? conversation.IsMutedForSeller : conversation.IsMutedForBuyer,
            conversation.SellerId == userId ? conversation.IsArchivedForSeller : conversation.IsArchivedForBuyer));
    }

    private async Task<ConversationDto?> LoadConversationDtoAsync(int conversationId, int userId)
    {
        var conversation = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Include(c => c.Ad).ThenInclude(a => a.Images)
            .Include(c => c.Seller)
            .Include(c => c.Buyer)
            .FirstOrDefaultAsync();

        if (conversation == null) return null;

        var (Count, FirstUnreadMessageId) = await writer.GetUnreadStateAsync(conversation, userId);
        return conversation.ToDto(userId, Count, FirstUnreadMessageId);
    }

    private async Task<Conversation?> GetOrCreateByAdAsync(int adId, int buyerId)
    {
        var sellerId = await db.Ads.AsNoTracking().Where(a => a.Id == adId).Select(a => (int?)a.UserId).FirstOrDefaultAsync();
        if (sellerId == null) return null;
        if (await IsBlockedBidirectionalAsync(buyerId, sellerId.Value)) return null;
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.AdId == adId && c.SellerId == sellerId && c.BuyerId == buyerId);
        if (conv != null) return conv;
        conv = new Conversation
        {
            AdId = adId,
            SellerId = sellerId.Value,
            BuyerId = buyerId
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        conv.DialogFolderPath = $"files/{sellerId.Value}/Ads/{adId}/dialogs/{conv.Id}";
        await db.SaveChangesAsync();
        Directory.CreateDirectory(Path.Combine(env.WebRootPath ?? "wwwroot", conv.DialogFolderPath));
        Directory.CreateDirectory(Path.Combine(env.WebRootPath ?? "wwwroot", conv.DialogFolderPath, "attachments"));
        return conv;
    }

    private static MessageType DetectMessageType(string ext) =>
        ext.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" => MessageType.Image,
            _ => MessageType.File
        };

    private async Task<ChatAttachment> SaveAttachmentAsync(Conversation conv, IFormFile file)
    {
        var webRoot = env.WebRootPath ?? "wwwroot";
        var attachFolder = Path.Combine(webRoot, conv.DialogFolderPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar), "attachments");
        Directory.CreateDirectory(attachFolder);

        var ext = Path.GetExtension(file.FileName);
        var msgType = DetectMessageType(ext);
        var isImage = msgType == MessageType.Image;
        var fileName = isImage ? $"{Guid.NewGuid():N}.jpg" : $"{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(attachFolder, fileName);

        await using var stream = file.OpenReadStream();
        if (isImage)
        {
            await imageService.SaveCompressedImageAsync(stream, attachFolder, fileName, targetKb: 50);
        }
        else
        {
            await using var output = System.IO.File.Create(fullPath);
            await stream.CopyToAsync(output);
        }

        var url = $"/{conv.DialogFolderPath}/attachments/{fileName}".Replace('\\', '/');
        return new ChatAttachment(url, msgType.ToString());
    }

    private bool IsBlockedBidirectional(int userA, int userB) =>
        db.UserBlocks.AsNoTracking().Any(b =>
            (b.SourceUserId == userA && b.TargetUserId == userB) ||
            (b.SourceUserId == userB && b.TargetUserId == userA));

    private Task<bool> IsBlockedBidirectionalAsync(int userA, int userB) =>
        db.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.SourceUserId == userA && b.TargetUserId == userB) ||
            (b.SourceUserId == userB && b.TargetUserId == userA));
}

public record CreateConversationRequest(int AdId);
public record SendMessageRequest(MessageType Type, string? Text, int? ReplyToMessageId);
public record EditMessageRequest(string? Text, List<ChatAttachment>? Attachments);

public static class ConversationExtensions
{
    public static bool IsParticipant(this Conversation c, int userId) =>
        c.SellerId == userId || c.BuyerId == userId;
}
