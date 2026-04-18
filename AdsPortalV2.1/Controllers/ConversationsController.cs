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
    MessageFlowService messageFlow,
    ConversationService conversationService,
    IConversationRepository conversations,
    IUserRepository userRepository,
    IWebHostEnvironment env,
    IHubContext<ChatHub> onlineHub,
    IHubContext<SystemNotificationHub> notificationHub,
    PermissionService perms,
    ILogger<ConversationsController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ConversationActionDto>> Create([FromBody] CreateConversationRequest req)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();

        // Comment ban: prevent starting conversations
        if (await perms.HasActiveRestrictionAsync(buyerId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));

        var ad = await conversations.GetAdByIdAsync(req.AdId);
        if (ad == null) return NotFound();

        var blockService = HttpContext.RequestServices.GetRequiredService<IBlockService>();
        if (!await blockService.CanSendAsync(buyerId, ad.UserId))
            return StatusCode(403, new ApiError("blocked", "User is blocked"));

        if (ad.UserId == buyerId)
            return BadRequest(new ApiError("validation_error", "Нельзя написать самому себе."));

        var existing = await conversations.FindByAdAndBuyerAsync(req.AdId, buyerId);
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

        await conversations.AddConversationAsync(conv);
        await conversations.SaveChangesAsync();

        conv.DialogFolderPath = $"files/{ad.UserId}/Ads/{ad.Id}/dialogs/{conv.Id}";
        await conversations.SaveChangesAsync();

        var buyerDto = await LoadConversationDtoAsync(conv.Id, buyerId);
        var sellerDto = conv.SellerId == conv.BuyerId ? buyerDto : await LoadConversationDtoAsync(conv.Id, conv.SellerId);

        if (buyerDto == null || sellerDto == null)
            return NotFound();

        // realtime: уведомим участников (buyer и seller) о новом диалоге
        try
        {
            // send to onlineHub for conversation realtime updates
            await onlineHub.Clients.Group($"conversation:{conv.Id}").SendAsync(HubEvents.ConversationCreated, sellerDto);
            if (conv.BuyerId != conv.SellerId)
                await onlineHub.Clients.Group($"conversation:{conv.Id}").SendAsync(HubEvents.ConversationCreated, buyerDto);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send chat:conversationCreated realtime notification for conversation {ConvId}", conv.Id);
        }

        return Ok(new ConversationActionDto(conv.Id, "created", buyerDto));
    }

    // Consolidated helper to upload attachments and create/send message for a given conversation
    private async Task<ActionResult<ConversationMessageActionDto>> UploadAttachmentsToConversationAsync(Conversation conv, int userId, IFormFileCollection files, string? text, int? replyToMessageId)
    {
        var contentText = text;
        var hasFiles = files.Count > 0;
        if (string.IsNullOrWhiteSpace(contentText) && Request.HasFormContentType)
            contentText = Request.Form["caption"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(contentText) && !hasFiles && replyToMessageId == null)
            return BadRequest(new ApiError("validation_error", "Empty message body"));

        if (!hasFiles)
            return await CreateAndSendMessage(conv.Id, userId, MessageType.Text, contentText, replyToMessageId);

        var fileValidationError = messageFlow.ValidateAttachments(files);
        if (fileValidationError != null)
            return BadRequest(new ApiError("validation_error", fileValidationError));

        var attachments = await Task.WhenAll(files.Select(f => messageFlow.SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == MessageType.Image) ? MessageType.Image : MessageType.File;
        return await CreateAndSendMessage(conv.Id, userId, type, contentText, replyToMessageId, attachments);
    }

    [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<ConversationDto>>> List()
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var convs = await conversations.GetForUserAsync(userId);
        convs = convs.Where(c =>
        {
            // hide conversations that the user has deleted up to some marker
            var marker = userId == c.SellerId
                ? c.SellerDeletedUpToMessageId
                : c.BuyerDeletedUpToMessageId;
            //return !IsBlockedBidirectional(c.SellerId, c.BuyerId) && (marker == null || c.TotalMessagesCount > marker.Value);
            return (marker == null || c.TotalMessagesCount > marker.Value);
        }).ToList();

        var list = await Task.WhenAll(convs.Select(async c =>
        {
            var (Count, FirstUnreadMessageId) = await writer.GetUnreadStateAsync(c, userId);
            return c.ToDto(userId, Count, FirstUnreadMessageId);
        }));

        return Ok(list);
    }

    [HttpGet("{id:int}/messages")]
    public async Task<ActionResult<ConversationMessagesDto>> GetMessages(int id, [FromQuery] int count = 10, [FromQuery] int? before = null, [FromQuery] int? since = null)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var isInitialLoad = before == null && since == null;

        var conv = isInitialLoad
            ? await conversations.GetByIdAsync(id, includeDetails: true, asNoTracking: true)
            : await conversations.GetByIdAsync(id, includeDetails: false, asNoTracking: true);

        if (conv == null) return NotFound();
        var accessResult = await EnsureCanAccessConversationAsync(userId, conv);
        if (accessResult != null) return (ActionResult<ConversationMessagesDto>)accessResult;

        List<ChatMessage> messages;
        bool hasMore;
        int? anchorMessageId = null;

        // Respect "deleted up to" marker set by participant: compute early so anchor can be adjusted
        var marker = userId == conv.SellerId
            ? conv.SellerDeletedUpToMessageId
            : conv.BuyerDeletedUpToMessageId;

        if (since.HasValue)
        {
            var cached = writer.GetCachedMessagesSince(conv.Id, since.Value + 1);
            (messages, hasMore) = cached ?? await reader.GetMessagesSinceAsync(conv, since.Value + 1);
        }
        else if (!isInitialLoad)
        {
            // If beforeId points into user's deleted zone, ignore it to avoid empty pages
            if (marker.HasValue && before.HasValue && before <= marker.Value)
                before = null;

            (messages, hasMore) = await reader.GetMessagesAsync(conv, count, before);
        }
        else
        {
            anchorMessageId = await writer.GetLastSeenMessageIdAsync(conv, userId);

            // If user's anchor (last seen) is at-or-before their deleted marker, ignore anchor so
            // we load messages from the beginning of the (visible) stream.
            if (marker.HasValue && anchorMessageId.HasValue && anchorMessageId <= marker.Value)
            {
                anchorMessageId = null;
            }

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

        if (marker.HasValue)
            messages = messages.Where(m => m.Id > marker.Value).ToList();

        var authorIds = messages
            .Select(m => m.AuthorId)
            .Distinct()
            .ToList();

        var userList = await userRepository.GetByIdsAsync(authorIds);
        var users = userList.ToDictionary(u => u.Id, u => new MessageAuthorDto(u.Id, u.UserName, u.UserLogin, u.AvatarPath));

        var enrichedMessages = messages.Select(m =>
        {
            if (!users.TryGetValue(m.AuthorId, out var author))
            {
                logger.LogWarning(
                    "Missing author in DB. ConversationId={ConversationId}, MessageId={MessageId}, AuthorId={AuthorId}",
                    id,
                    m.Id,
                    m.AuthorId
                );
                author = new MessageAuthorDto(m.AuthorId, null, "[deleted]", null);
            }

            return BuildConversationMessageDto(id, m, author);
        }).ToList();

        // при initial load пометим сообщения как прочитанные для текущего пользователя,
        // чтобы счётчик непрочитанных синхронизировался сразу при открытии диалога
        if (isInitialLoad && messages.Count > 0)
        {
            var lastMsgId = await reader.GetLatestMessageIdAsync(conv) ?? messages.Max(m => m.Id);

            // If user deleted chat up to marker, ensure we mark as read at least up to marker
            if (marker.HasValue)
                lastMsgId = Math.Max(lastMsgId, marker.Value);

            await writer.MarkAsReadAsync(conv.Id, userId, lastMsgId);
        }

        var conversation = await LoadConversationDtoAsync(conv.Id, userId);
        if (conversation == null) return NotFound();

        var opponentId = conv.SellerId == userId ? conv.BuyerId : conv.SellerId;
        var myLastSeenMessageId = anchorMessageId;
        var otherLastSeenMessageId = await writer.GetLastSeenMessageIdAsync(conv, opponentId);

        return Ok(new ConversationMessagesDto(conversation, enrichedMessages, hasMore, anchorMessageId, myLastSeenMessageId, otherLastSeenMessageId));
    }

    // Helper: build ConversationMessageDto from internal ChatMessage (delegates to DialogHelpers)
    private ConversationMessageDto BuildConversationMessageDto(int conversationId, ChatMessage msg, MessageAuthorDto? author)
        => DialogHelpers.ToConversationMessageDto(conversationId, msg, author);

    private async Task<MessageAuthorDto> GetAuthorDtoAsync(int authorId)
    {
        var a = await db.Users.AsNoTracking()
            .Where(u => u.Id == authorId)
            .Select(u => new { u.Id, u.UserName, u.UserLogin, u.AvatarPath })
            .FirstOrDefaultAsync();

        if (a == null) return new MessageAuthorDto(authorId, null, "[deleted]", null);
        return new MessageAuthorDto(a.Id, a.UserName, a.UserLogin, a.AvatarPath);
    }

    // Consolidated helper: build DTO, send realtime notification(s) and return ActionResult
    private async Task<ActionResult<ConversationMessageActionDto>> SendMessageAndNotifyAsync(int conversationId, ChatMessage msg, MessageAuthorDto? author = null)
    {
        var authorDto = author ?? await GetAuthorDtoAsync(msg.AuthorId);
        var convMessage = BuildConversationMessageDto(conversationId, msg, authorDto);
        var action = new ConversationMessageActionDto(conversationId, convMessage);
        try
        {
            // Per-recipient delivery: respect per-user deleted markers so users who cleared history
            // do not receive old messages.
            var conv = await db.Conversations.AsNoTracking()
                .Where(c => c.Id == conversationId)
                .Select(c => new { c.Id, c.SellerId, c.BuyerId, c.SellerDeletedUpToMessageId, c.BuyerDeletedUpToMessageId, c.IsMutedForSeller, c.IsMutedForBuyer })
                .FirstOrDefaultAsync();

            if (conv != null)
            {
                var participants = new[] { conv.SellerId, conv.BuyerId }.Distinct();
                foreach (var u in participants)
                {
                    var marker = u == conv.SellerId ? conv.SellerDeletedUpToMessageId : conv.BuyerDeletedUpToMessageId;
                    if (marker == null || msg.Id > marker.Value)
                    {
                        // Always send the message event (delivery)
                        try { await onlineHub.Clients.Group($"user:{u}").SendAsync(HubEvents.Message, convMessage); } catch { }

                        // Emit notification intent event separately. Include IsMuted flag so client decides presentation.
                        bool isMuted = (u == conv.SellerId) ? conv.IsMutedForSeller : conv.IsMutedForBuyer;
                        var candidate = new MessageNotificationCandidateEvent(conversationId, msg.Id, msg.AuthorId, msg.CreatedAt, isMuted);
                        try { await notificationHub.Clients.Group($"user:{u}").SendAsync(HubEvents.MessageNotificationCandidate, candidate); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send realtime notification for conversation {ConvId}", conversationId);
        }

        return Ok(action);
    }

    // Consolidated helper to enqueue a message and send realtime notifications
    private async Task<ActionResult<ConversationMessageActionDto>> CreateAndSendMessage(int conversationId, int userId, MessageType type, string? text, int? replyToMessageId, IEnumerable<ChatAttachment>? attachments = null, string? clientTag = null)
    {
        var conv = await db.Conversations.FindAsync(conversationId);
        if (conv == null) return NotFound();
        var blockService = HttpContext.RequestServices.GetRequiredService<IBlockService>();
        if (!await blockService.CanSendAsync(conv.SellerId, conv.BuyerId)) return StatusCode(403, new ApiError("blocked", "User is blocked"));

        var list = attachments?.ToList();
        var msg = await writer.EnqueueAsync(conversationId, userId, type, text, replyToMessageId, list, clientTag);
        return await SendMessageAndNotifyAsync(conversationId, msg);
    }

    [HttpPost("{id:int}/messages")]
    [EnableRateLimiting("MessagesPerSecond")]
    [Consumes("application/json")]
    public async Task<ActionResult<ConversationMessageActionDto>> SendMessage(int id, [FromBody] SendMessageRequest req)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        // Comment ban: prevent sending messages
        if (await perms.HasActiveRestrictionAsync(userId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));

        var conv = await conversations.GetByIdAsync(id, includeDetails: false, asNoTracking: true);
        if (conv == null) return NotFound();

        var accessResult = await EnsureCanAccessConversationAsync(userId, conv);
        if (accessResult != null) return accessResult;

        var pipeline = HttpContext.RequestServices.GetRequiredService<MessagePipeline>();
        var ctx = new MessageContext { SenderId = userId, ConversationId = conv.Id, Text = req.Text };
        await pipeline.ExecuteAsync(ctx);
        if (ctx.IsRejected) return StatusCode(403, new ApiError(ctx.ErrorCode ?? "blocked", "User is blocked"));

        if (string.IsNullOrWhiteSpace(req.Text) && req.ReplyToMessageId == null && req.Type == MessageType.Text)
            return BadRequest(new ApiError("validation_error", "Empty message body"));

        // ClientTag is part of SendMessageRequest DTO — use it directly
        return await CreateAndSendMessage(id, userId, req.Type, req.Text, req.ReplyToMessageId, null, req.ClientTag);
    }

    [HttpPost("{id:int}/messages/upload")]
    [EnableRateLimiting("MessagesPerSecond")]
    [Consumes("multipart/form-data")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<ActionResult<ConversationMessageActionDto>> SendMessage(int id, [FromForm] string? text, [FromForm] int? replyToMessageId, [FromForm(Name = "files")] IFormFileCollection files, [FromForm] string? clientTag)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        // Comment ban: prevent sending messages
        if (await perms.HasActiveRestrictionAsync(userId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));

        var conv = await conversations.GetByIdAsync(id, includeDetails: false, asNoTracking: true);
        if (conv == null) return NotFound();
        var accessResult = await EnsureCanAccessConversationAsync(userId, conv);
        if (accessResult != null) return accessResult;

        // Some clients send the text
        var contentText = text;
        if (string.IsNullOrWhiteSpace(contentText) && Request.HasFormContentType)
            contentText = Request.Form["caption"].FirstOrDefault();

        var pipeline = HttpContext.RequestServices.GetRequiredService<MessagePipeline>();
        var ctx = new MessageContext { SenderId = userId, ConversationId = conv.Id, Text = contentText };
        await pipeline.ExecuteAsync(ctx);
        if (ctx.IsRejected) return StatusCode(403, new ApiError(ctx.ErrorCode ?? "blocked", "User is blocked"));

        var hasFiles = files.Count > 0;
        if (string.IsNullOrWhiteSpace(contentText) && !hasFiles && replyToMessageId == null)
            return BadRequest(new ApiError("validation_error", "Empty message body"));

        if (!hasFiles)
            return await CreateAndSendMessage(id, userId, MessageType.Text, contentText, replyToMessageId, null, clientTag);

        var fileValidationError = messageFlow.ValidateAttachments(files);
        if (fileValidationError != null)
            return BadRequest(new ApiError("validation_error", fileValidationError));

        var attachments = await Task.WhenAll(files.Select(f => messageFlow.SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == MessageType.Image) ? MessageType.Image : MessageType.File;
        return await CreateAndSendMessage(id, userId, type, contentText, replyToMessageId, attachments, clientTag);
    }

    [HttpPost("by-ad/{adId:int}/messages")]
    [EnableRateLimiting("MessagesPerSecond")]
    [Consumes("application/json")]
    public async Task<ActionResult<ConversationMessageActionDto>> SendMessageByAd(int adId, [FromBody] SendMessageRequest req)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();
        return await CreateAndSendMessageByAdAsync(adId, buyerId, req.Type, req.Text, req.ReplyToMessageId);
    }

    [HttpPost("by-ad/{adId:int}/messages/upload")]
    [EnableRateLimiting("MessagesPerSecond")]
    [Consumes("multipart/form-data")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<ActionResult<ConversationMessageActionDto>> SendMessageByAd(int adId, [FromForm] string? text, [FromForm] int? replyToMessageId, [FromForm(Name = "files")] IFormFileCollection files)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();
        return await UploadAttachmentsByAdAsync(adId, buyerId, files, text, replyToMessageId);
    }

    [HttpPatch("{id:int}/read")]
    public async Task<ActionResult> MarkAsRead(int id, [FromQuery] int? lastSeenMessageId)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        if (!lastSeenMessageId.HasValue) return BadRequest(new ApiError("validation_error", "lastSeenMessageId is required"));

        var conv = await db.Conversations.FindAsync(id);
        if (conv == null) return NotFound();
        var accessResult = await EnsureCanAccessConversationAsync(userId, conv);
        if (accessResult != null) return accessResult;

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
    public async Task<ActionResult> ToggleMute(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.FindAsync(id);
        if (conv == null) return NotFound();
        var accessResult = await EnsureCanAccessConversationAsync(userId, conv);
        if (accessResult != null) return accessResult;

        if (userId == conv.SellerId) conv.IsMutedForSeller = !conv.IsMutedForSeller;
        else conv.IsMutedForBuyer = !conv.IsMutedForBuyer;

        await db.SaveChangesAsync();
        return Ok();
    }

    [HttpPatch("{id:int}/archive")]
    public async Task<ActionResult> ToggleArchive(int id)
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
    public async Task<ActionResult<ConversationMessageActionDto>> EditMessage(int id, int messageId, [FromBody] EditMessageRequest req)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId))
            return StatusCode(403, new ApiError("forbidden", "Недостаточно прав для доступа к этому диалогу."));
        var blockService = HttpContext.RequestServices.GetRequiredService<IBlockService>();
        if (!await blockService.CanSendAsync(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        var existing = await reader.FindByIdAsync(conv, messageId);
        if (existing == null) return NotFound();
        if (existing.AuthorId != userId)
            return StatusCode(403, new ApiError("forbidden", "Вы не являетесь автором этого сообщения."));

        var updated = await writer.EnqueuePatchAsync(id, messageId, req.Text, req.Attachments);
        return await SendMessageAndNotifyAsync(id, updated);
    }

    [HttpPost("{id:int}/messages/{messageId:int}/attachments")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<ActionResult<ConversationMessageActionDto>> AddMessageAttachment(int id, int messageId, [FromForm(Name = "files")] IFormFileCollection files)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId))
            return StatusCode(403, new ApiError("forbidden", "Недостаточно прав для доступа к этому диалогу."));
        var blockService = HttpContext.RequestServices.GetRequiredService<IBlockService>();
        if (!await blockService.CanSendAsync(conv.SellerId, conv.BuyerId))
            return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));

        var existing = await reader.FindByIdAsync(conv, messageId);
        if (existing == null) return NotFound();
        if (existing.AuthorId != userId)
            return StatusCode(403, new ApiError("forbidden", "Вы не являетесь автором этого сообщения."));

        var fileValidationError = messageFlow.ValidateAttachments(files);
        if (fileValidationError != null)
            return BadRequest(new ApiError("validation_error", fileValidationError));

        var newAttachments = await Task.WhenAll(files.Select(f => messageFlow.SaveAttachmentAsync(conv, f)));
        var combined = (existing.Attachments ?? []).Concat(newAttachments).ToList();
        var updated = await writer.EnqueuePatchAsync(id, messageId, null, combined);
        return await SendMessageAndNotifyAsync(id, updated);
    }

    [HttpDelete("{id:int}/messages/{messageId:int}")]
    public async Task<ActionResult<ConversationMessageActionDto>> DeleteMessage(int id, int messageId)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await conversations.GetByIdAsync(id, includeDetails: false, asNoTracking: true);
        if (conv == null) return NotFound();
        var accessResult = await EnsureCanAccessConversationAsync(userId, conv);
        if (accessResult != null) return accessResult;

        var existing = await reader.FindByIdAsync(conv, messageId);
        if (existing == null) return NotFound();
        if (existing.AuthorId != userId) return Forbid();

        var updated = await writer.EnqueueDeleteAsync(id, messageId);
        return await SendMessageAndNotifyAsync(id, updated);
    }

    [HttpPost("{id:int}/attachments")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<ActionResult<ConversationMessageActionDto>> UploadAttachment(int id, [FromForm(Name = "files")] IFormFileCollection files, [FromForm] string? caption)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();
        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        var accessResult = await EnsureCanAccessConversationAsync(userId, conv);
        if (accessResult != null) return accessResult;

        return await UploadAttachmentsToConversationAsync(conv, userId, files, caption, null);
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult> DeleteConversation(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.FindAsync(id);
        if (conv == null) return NotFound();
        if (!conv.IsParticipant(userId)) return Forbid();

        // получаем последнее сообщение
        var lastMessageId = await reader.GetLatestMessageIdAsync(conv);
        if (lastMessageId == null) return Ok(); // диалог пустой

        logger.LogWarning("DELETE: conv={Id}, lastMessageId={LastId}, total={Total}",
    conv.Id, lastMessageId, conv.TotalMessagesCount);

        if (userId == conv.SellerId)
            conv.SellerDeletedUpToMessageId = lastMessageId;
        else
            conv.BuyerDeletedUpToMessageId = lastMessageId;

        await db.SaveChangesAsync();
        return NoContent();
    }

    // Shared helpers for by-ad endpoints to avoid duplication
    private async Task<ActionResult<ConversationMessageActionDto>> CreateAndSendMessageByAdAsync(int adId, int buyerId, MessageType type, string? text, int? replyToMessageId)
    {
        if (await perms.HasActiveRestrictionAsync(buyerId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));
        var conv = await GetOrCreateByAdAsync(adId, buyerId);
        if (conv == null) return NotFound();
        return await CreateAndSendMessage(conv.Id, buyerId, type, text, replyToMessageId);
    }

    private async Task<ActionResult<ConversationMessageActionDto>> UploadAttachmentsByAdAsync(int adId, int buyerId, IFormFileCollection files, string? text, int? replyToMessageId)
    {
        if (await perms.HasActiveRestrictionAsync(buyerId, RestrictionType.ChatBan))
            return StatusCode(403, new ApiError("chat_banned", "You cannot send messages"));
        var conv = await GetOrCreateByAdAsync(adId, buyerId);
        if (conv == null) return NotFound();
        var contentText = string.IsNullOrWhiteSpace(text) ? Request.Form["caption"].FirstOrDefault() : text;
        return await UploadAttachmentsToConversationAsync(conv, buyerId, files, contentText, replyToMessageId);
    }

    [HttpPost("by-ad/{adId:int}/attachments")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<ActionResult<ConversationMessageActionDto>> UploadAttachmentByAd(int adId, [FromForm(Name = "files")] IFormFileCollection files, [FromForm] string? caption)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();
        var conv = await GetOrCreateByAdAsync(adId, buyerId);
        if (conv == null) return NotFound();
        return await UploadAttachmentsToConversationAsync(conv, buyerId, files, caption, null);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ConversationStateDto>> GetConversation(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();
        var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conversation == null) return NotFound();
        var accessResult = await EnsureCanAccessConversationAsync(userId, conversation);
        if (accessResult != null) return accessResult;
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

    private Task<Conversation?> GetOrCreateByAdAsync(int adId, int buyerId) =>
        conversationService.GetOrCreateByAdAsync(db, adId, buyerId);

    // Consolidated access check used across controller to avoid duplication of participant/block checks
    private async Task<ActionResult?> EnsureCanAccessConversationAsync(int userId, Conversation conv)
    {
        if (!conv.IsParticipant(userId)) return Forbid();
        //if (await IsBlockedBidirectionalAsync(conv.SellerId, conv.BuyerId))
        //    return StatusCode(403, new ApiError("forbidden", "Диалог недоступен."));
        return null;
    }

    // Helper: whether participants are allowed to send messages (not mutually blocked)
    private async Task<bool> CanSendAsync(Conversation conv)
    {
        var blockService = HttpContext.RequestServices.GetRequiredService<IBlockService>();
        return await blockService.CanSendAsync(conv.SellerId, conv.BuyerId);
    }

}

public record CreateConversationRequest(int AdId);
public record EditMessageRequest(string? Text, List<ChatAttachment>? Attachments);

public static class ConversationExtensions
{
    public static bool IsParticipant(this Conversation c, int userId) =>
        c.SellerId == userId || c.BuyerId == userId;
}
