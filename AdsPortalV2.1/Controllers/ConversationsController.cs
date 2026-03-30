using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("conversations")]
[Authorize]
public class ConversationsController(
    AppDbContext db,
    DialogWriterService writer,
    DialogReaderService reader,
    ImageService imageService,
    IWebHostEnvironment env) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConversationRequest req)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();

        var ad = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.AdId);
        if (ad == null) return NotFound();

        if (ad.UserId == buyerId)
            return BadRequest(new { Message = "Нельзя написать самому себе." });

        var existing = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AdId == req.AdId && c.BuyerId == buyerId);
        if (existing != null) return Ok(new { existing.Id });

        var conv = new Conversation
        {
            AdId = ad.Id,
            SellerId = ad.UserId,
            BuyerId = buyerId
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        conv.DialogFolderPath = $"files/{conv.SellerId}/Ads/{conv.AdId}/dialogsFolder/{conv.Id}";
        await db.SaveChangesAsync();

        return Created($"/conversations/{conv.Id}", new { conv.Id });
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conversations = await db.Conversations
            .AsNoTracking()
            .Where(c => c.SellerId == userId || c.BuyerId == userId)
            .OrderByDescending(c => c.LastMessageTimestamp)
            .Select(c => new
            {
                c.Id,
                c.AdId,
                c.CreatedAt,
                c.IsClosed,
                c.LastMessageTimestamp,
                c.LastMessageType,
                c.LastMessageText,
                c.LastMessageAuthorId,
                c.TotalMessagesCount,
                c.SellerId,
                c.BuyerId,
                c.DialogFolderPath,
                c.IsMutedForSeller,
                c.IsMutedForBuyer,
                c.IsArchivedForSeller,
                c.IsArchivedForBuyer,
                Seller = new { c.Seller.Id, c.Seller.UserName, c.Seller.UserLogin },
                Buyer = new { c.Buyer.Id, c.Buyer.UserName, c.Buyer.UserLogin },
                Ad = new
                {
                    c.Ad.Id,
                    c.Ad.Title,
                    c.Ad.ModerationStatus,
                    MainImagePath = c.Ad.Images.Where(img => img.IsMain == true).Select(img => img.FilePath).FirstOrDefault()
                }
            })
            .ToListAsync();

        var list = await Task.WhenAll(conversations.Select(async c =>
        {
            var unread = await writer.GetUnreadStateAsync(c.DialogFolderPath, c.SellerId, c.BuyerId, userId);
            return new
            {
                c.Id,
                c.AdId,
                c.CreatedAt,
                c.IsClosed,
                c.LastMessageTimestamp,
                c.LastMessageType,
                c.LastMessageText,
                c.LastMessageAuthorId,
                c.TotalMessagesCount,
                HasUnread = unread.Count > 0,
                UnreadCount = unread.Count,
                FirstUnreadMessageId = unread.FirstUnreadMessageId,
                IsMuted = c.SellerId == userId ? c.IsMutedForSeller : c.IsMutedForBuyer,
                IsArchived = c.SellerId == userId ? c.IsArchivedForSeller : c.IsArchivedForBuyer,
                Opponent = c.SellerId == userId ? c.Buyer : c.Seller,
                Ad = c.Ad
            };
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
        if (conv.SellerId != userId && conv.BuyerId != userId) return Forbid();

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

        var users = await db.Users
            .Where(u => messages.Select(m => m.AuthorId).Distinct().Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.UserName, u.UserLogin, u.AvatarPath });

        var enrichedMessages = messages.Select(m => new
        {
            m.Id, m.Type, m.AuthorId,
            Author = users.GetValueOrDefault(m.AuthorId),
            m.CreatedAt, m.Text, Attachments = m.Attachments ?? [], m.ReplyToMessageId, m.EditedAt, m.DeletedAt
        });

        if (!isInitialLoad)
            return Ok(new { Messages = enrichedMessages, HasMore = hasMore });

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

        return Ok(new
        {
            Conversation = new
            {
                conv.Id,
                conv.AdId,
                Ad = new
                {
                    conv.Ad.Id,
                    conv.Ad.Title,
                    conv.Ad.Price,
                    conv.Ad.CreatedAt,
                    conv.Ad.ModerationStatus,
                    MainImagePath = conv.Ad.Images.FirstOrDefault(img => img.IsMain)?.FilePath
                },
                Me = new { me.Id, me.UserName, me.UserLogin, me.AvatarPath, me.LastActivityAt },
                Opponent = new { opponent.Id, opponent.UserName, opponent.UserLogin, opponent.AvatarPath, opponent.LastActivityAt },
                conv.CreatedAt,
                conv.IsClosed,
                LastMessageText = conv.LastMessageText
            },
            Messages = enrichedMessages,
            HasMore = hasMore,
            AnchorMessageId = anchorMessageId,
            MyLastSeenMessageId = myLastSeenMessageId,
            OtherLastSeenMessageId = otherLastSeenMessageId
        });
    }

    [HttpPost("{id:int}/messages")]
    [Consumes("application/json")]
    public async Task<IActionResult> SendMessage(int id, [FromBody] SendMessageRequest req)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (conv.SellerId != userId && conv.BuyerId != userId) return Forbid();

        if (string.IsNullOrWhiteSpace(req.Text) && req.ReplyToMessageId == null && req.Type == MessageType.Text)
            return BadRequest(new { error = "Empty message body" });

        var msg = await writer.EnqueueAsync(id, userId, req.Type, req.Text, req.ReplyToMessageId);
        return Ok(msg);
    }

    [HttpPost("{id:int}/messages")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> SendMessage(int id, [FromForm] string? text, [FromForm] int? replyToMessageId, [FromForm] List<IFormFile>? files)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (conv.SellerId != userId && conv.BuyerId != userId) return Forbid();

        // Some clients send the text under the field name "caption" when uploading files.
        var contentText = text;
        if (string.IsNullOrWhiteSpace(contentText) && Request.HasFormContentType)
            contentText = Request.Form["caption"].FirstOrDefault();

        var hasFiles = files is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(contentText) && !hasFiles && replyToMessageId == null)
            return BadRequest(new { error = "Empty message body" });

        if (!hasFiles)
        {
            var message = await writer.EnqueueAsync(id, userId, MessageType.Text, contentText, replyToMessageId);
            return Ok(message);
        }

        var attachments = await Task.WhenAll(files!.Select(f => SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == nameof(MessageType.Image)) ? MessageType.Image : MessageType.File;
        var msg = await writer.EnqueueAsync(id, userId, type, contentText, replyToMessageId, [.. attachments]);
        return Ok(msg);
    }

    [HttpPost("by-ad/{adId:int}/messages")]
    [Consumes("application/json")]
    public async Task<IActionResult> SendMessageByAd(int adId, [FromBody] SendMessageRequest req)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();
        var conv = await GetOrCreateByAdAsync(adId, buyerId);
        if (conv == null) return NotFound();
        var msg = await writer.EnqueueAsync(conv.Id, buyerId, req.Type, req.Text, req.ReplyToMessageId);
        return Ok(new { conversationId = conv.Id, message = msg });
    }

    [HttpPost("by-ad/{adId:int}/messages")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> SendMessageByAd(int adId, [FromForm] string? text, [FromForm] int? replyToMessageId, [FromForm] List<IFormFile>? files)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();
        var conv = await GetOrCreateByAdAsync(adId, buyerId);
        if (conv == null) return NotFound();
        var contentText = string.IsNullOrWhiteSpace(text) ? Request.Form["caption"].FirstOrDefault() : text;
        var hasFiles = files is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(contentText) && !hasFiles && replyToMessageId == null)
            return BadRequest(new { error = "Empty message body" });
        if (!hasFiles)
            return Ok(new { conversationId = conv.Id, message = await writer.EnqueueAsync(conv.Id, buyerId, MessageType.Text, contentText, replyToMessageId) });
        var attachments = await Task.WhenAll(files!.Select(f => SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == nameof(MessageType.Image)) ? MessageType.Image : MessageType.File;
        return Ok(new { conversationId = conv.Id, message = await writer.EnqueueAsync(conv.Id, buyerId, type, contentText, replyToMessageId, [.. attachments]) });
    }

    [HttpPatch("{id:int}/read")]
    public async Task<IActionResult> MarkAsRead(int id, [FromQuery] int? lastSeenMessageId)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        if (!lastSeenMessageId.HasValue) return BadRequest(new { error = "lastSeenMessageId is required" });

        var conv = await db.Conversations.FindAsync(id);
        if (conv == null) return NotFound();
        if (conv.SellerId != userId && conv.BuyerId != userId) return Forbid();

        await writer.MarkAsReadAsync(id, userId, lastSeenMessageId.Value);
        return Ok();
    }

    [HttpPatch("{id:int}/mute")]
    public async Task<IActionResult> ToggleMute(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.FindAsync(id);
        if (conv == null) return NotFound();
        if (conv.SellerId != userId && conv.BuyerId != userId) return Forbid();

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
        if (conv.SellerId != userId && conv.BuyerId != userId) return Forbid();

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
        if (conv.SellerId != userId && conv.BuyerId != userId)
            return StatusCode(403, new { error = "Недостаточно прав для доступа к этому диалогу." });

        var existing = await reader.FindByIdAsync(conv, messageId);
        if (existing == null) return NotFound();
        if (existing.AuthorId != userId)
            return StatusCode(403, new { error = "Вы не являетесь автором этого сообщения." });

        var updated = await writer.EnqueuePatchAsync(id, messageId, req.Text, req.Attachments);
        return Ok(updated);
    }

    [HttpPost("{id:int}/messages/{messageId:int}/attachments")]
    public async Task<IActionResult> AddMessageAttachment(int id, int messageId, [FromForm] List<IFormFile> files)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (conv.SellerId != userId && conv.BuyerId != userId)
            return StatusCode(403, new { error = "Недостаточно прав для доступа к этому диалогу." });

        var existing = await reader.FindByIdAsync(conv, messageId);
        if (existing == null) return NotFound();
        if (existing.AuthorId != userId)
            return StatusCode(403, new { error = "Вы не являетесь автором этого сообщения." });

        var newAttachments = await Task.WhenAll(files.Select(f => SaveAttachmentAsync(conv, f)));
        var combined = (existing.Attachments ?? []).Concat(newAttachments).ToList();
        var updated = await writer.EnqueuePatchAsync(id, messageId, null, combined);
        return Ok(updated);
    }

    [HttpDelete("{id:int}/messages/{messageId:int}")]
    public async Task<IActionResult> DeleteMessage(int id, int messageId)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (conv.SellerId != userId && conv.BuyerId != userId) return Forbid();

        var existing = await reader.FindByIdAsync(conv, messageId);
        if (existing == null) return NotFound();
        if (existing.AuthorId != userId) return Forbid();

        var updated = await writer.EnqueueDeleteAsync(id, messageId);
        return Ok(updated);
    }

    [HttpPost("{id:int}/attachments")]
    public async Task<IActionResult> UploadAttachment(int id, [FromForm] List<IFormFile> files, [FromForm] string? caption)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return NotFound();
        if (conv.SellerId != userId && conv.BuyerId != userId) return Forbid();

        var attachments = await Task.WhenAll(files.Select(f => SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == nameof(MessageType.Image)) ? MessageType.Image : MessageType.File;
        var msg = await writer.EnqueueAsync(id, userId, type, caption, attachments: [.. attachments]);
        return Ok(msg);
    }

    [HttpPost("by-ad/{adId:int}/attachments")]
    public async Task<IActionResult> UploadAttachmentByAd(int adId, [FromForm] List<IFormFile> files, [FromForm] string? caption)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();
        var conv = await GetOrCreateByAdAsync(adId, buyerId);
        if (conv == null) return NotFound();
        var attachments = await Task.WhenAll(files.Select(f => SaveAttachmentAsync(conv, f)));
        var type = attachments.All(a => a.Type == nameof(MessageType.Image)) ? MessageType.Image : MessageType.File;
        return Ok(new { conversationId = conv.Id, message = await writer.EnqueueAsync(conv.Id, buyerId, type, caption, attachments: [.. attachments]) });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetConversation(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();
        var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conversation == null) return NotFound();
        if (conversation.SellerId != userId && conversation.BuyerId != userId) return Forbid();
        var unread = await writer.GetUnreadStateAsync(conversation, userId);
        var opponentId = conversation.SellerId == userId ? conversation.BuyerId : conversation.SellerId;
        var myLastSeenMessageId = await writer.GetLastSeenMessageIdAsync(conversation, userId);
        var otherLastSeenMessageId = await writer.GetLastSeenMessageIdAsync(conversation, opponentId);
        return Ok(new
        {
            conversation.Id, conversation.AdId, conversation.SellerId, conversation.BuyerId,
            conversation.CreatedAt, conversation.IsClosed, conversation.LastMessageTimestamp,
            conversation.LastMessageType, conversation.LastMessageText, conversation.LastMessageAuthorId,
            conversation.TotalMessagesCount,
            HasUnread = unread.Count > 0, UnreadCount = unread.Count,
            MyLastSeenMessageId = myLastSeenMessageId,
            OtherLastSeenMessageId = otherLastSeenMessageId,
            IsMuted = conversation.SellerId == userId ? conversation.IsMutedForSeller : conversation.IsMutedForBuyer,
            IsArchived = conversation.SellerId == userId ? conversation.IsArchivedForSeller : conversation.IsArchivedForBuyer
        });
    }

    private async Task<Conversation?> GetOrCreateByAdAsync(int adId, int buyerId)
    {
        var sellerId = await db.Ads.AsNoTracking().Where(a => a.Id == adId).Select(a => (int?)a.UserId).FirstOrDefaultAsync();
        if (sellerId == null) return null;
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.AdId == adId && c.SellerId == sellerId && c.BuyerId == buyerId);
        if (conv != null) return conv;
        conv = new Conversation { AdId = adId, SellerId = sellerId.Value, BuyerId = buyerId };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        conv.DialogFolderPath = $"files/{conv.SellerId}/Ads/{conv.AdId}/dialogsFolder/{conv.Id}";
        Directory.CreateDirectory(Path.Combine(env.WebRootPath ?? "wwwroot", conv.DialogFolderPath));
        await db.SaveChangesAsync();
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
        var attachFolder = Path.Combine(webRoot, "files", conv.SellerId.ToString(),
            "Ads", conv.AdId.ToString(), "dialogsFolder", conv.Id.ToString(), "attachments");

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
            Directory.CreateDirectory(attachFolder);
            await using var output = System.IO.File.Create(fullPath);
            await stream.CopyToAsync(output);
        }

        var url = $"/files/{conv.SellerId}/Ads/{conv.AdId}/dialogsFolder/{conv.Id}/attachments/{fileName}";
        return new ChatAttachment(url, msgType.ToString());
    }
}

public record CreateConversationRequest(int AdId);
public record SendMessageRequest(MessageType Type, string? Text, int? ReplyToMessageId);
public record EditMessageRequest(string? Text, List<ChatAttachment>? Attachments);
