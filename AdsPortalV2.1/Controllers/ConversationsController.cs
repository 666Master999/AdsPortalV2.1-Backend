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
                UnreadMessagesCount = unread.Count,
                FirstUnreadMessageId = unread.FirstUnreadMessageId,
                UnreadClusterStartMessageId = unread.FirstUnreadMessageId.HasValue ? (int?)Math.Max(unread.FirstUnreadMessageId.Value - 10, 1) : null,
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
            // pull-синхронизация после reconnect: все сообщения с id > since
            var result = await reader.GetMessagesSinceAsync(conv, since.Value + 1);
            messages = result.Messages;
            hasMore = result.HasMore;
        }
        else if (isInitialLoad)
        {
            var lastSeen = await writer.GetLastSeenMessageIdAsync(conv, userId);
            var unread = await writer.GetUnreadStateAsync(conv, userId);
            var unreadStart = unread.Item1 > 0 && unread.Item2.HasValue
                ? Math.Max(unread.Item2.Value - 10, 1)
                : (int?)null;

            if (lastSeen.HasValue)
            {
                var result = await reader.GetMessagesAsync(conv, count, lastSeen.Value + 1);
                messages = result.Messages;
                hasMore = result.HasMore;
                anchorMessageId = lastSeen.Value;
            }
            else if (unreadStart.HasValue)
            {
                var result = await reader.GetMessagesSinceAsync(conv, unreadStart.Value);
                messages = result.Messages;
                hasMore = result.HasMore;
            }
            else
            {
                var result = await reader.GetMessagesAsync(conv, count, null);
                messages = result.Messages;
                hasMore = result.HasMore;
            }
        }
        else
        {
            var result = await reader.GetMessagesAsync(conv, count, before);
            messages = result.Messages;
            hasMore = result.HasMore;
        }

        var users = await db.Users
            .Where(u => messages.Select(m => m.AuthorId).Distinct().Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.UserName, u.UserLogin, u.AvatarPath });

        var attachmentMetaTasks = messages
            .Where(m => m.Attachments != null && m.Attachments.Count > 0)
            .Select(async m => new { m.Id, Meta = await writer.GetAttachmentsMetadataAsync(conv, m.Attachments) })
            .ToList();

        var attachmentMetas = (await Task.WhenAll(attachmentMetaTasks)).ToDictionary(x => x.Id, x => x.Meta);

        var enrichedMessages = messages.Select(m => new
        {
            m.Id,
            m.Type,
            m.AuthorId,
            Author = users.TryGetValue(m.AuthorId, out var user) ? user : null,
            m.CreatedAt,
            m.Text,
            m.Attachments,
            AttachmentsMeta = attachmentMetas.TryGetValue(m.Id, out var meta) ? meta : null,
            m.IsRead,
            m.ReadAt,
            m.ReplyToMessageId,
            m.EditedAt,
            m.DeletedAt
        });

        if (!isInitialLoad)
            return Ok(new { Messages = enrichedMessages, HasMore = hasMore });

        var me = conv.SellerId == userId ? conv.Seller : conv.Buyer;
        var opponent = conv.SellerId == userId ? conv.Buyer : conv.Seller;

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
            AnchorMessageId = anchorMessageId
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
        var type = attachments.All(path => DetectMessageType(Path.GetExtension(path)) == MessageType.Image)
            ? MessageType.Image
            : MessageType.File;

        var msg = await writer.EnqueueAsync(id, userId, type, contentText, replyToMessageId, [.. attachments]);
        return Ok(msg);
    }

    [HttpPost("by-ad/{adId:int}/messages")]
    [Consumes("application/json")]
    public async Task<IActionResult> SendMessageByAd(int adId, [FromBody] SendMessageRequest req)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();

        var ad = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == adId);
        if (ad == null) return NotFound(new { error = "Ad not found", adId });

        var sellerId = ad.UserId;

        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.AdId == adId && c.SellerId == sellerId && c.BuyerId == buyerId);

        if (conversation == null)
        {
            conversation = new Conversation
            {
                AdId = adId,
                SellerId = sellerId,
                BuyerId = buyerId
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();

            conversation.DialogFolderPath = $"files/{conversation.SellerId}/Ads/{conversation.AdId}/dialogsFolder/{conversation.Id}";
            Directory.CreateDirectory(Path.Combine(env.WebRootPath ?? "wwwroot", conversation.DialogFolderPath));
            await db.SaveChangesAsync();
        }

        var message = await writer.EnqueueAsync(conversation.Id, buyerId, req.Type, req.Text, req.ReplyToMessageId);

        return Ok(new
        {
            conversationId = conversation.Id,
            message
        });
    }

    [HttpPost("by-ad/{adId:int}/messages")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> SendMessageByAd(int adId, [FromForm] string? text, [FromForm] int? replyToMessageId, [FromForm] List<IFormFile>? files)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();

        var ad = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == adId);
        if (ad == null) return NotFound(new { error = "Ad not found", adId });

        var sellerId = ad.UserId;

        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.AdId == adId && c.SellerId == sellerId && c.BuyerId == buyerId);

        if (conversation == null)
        {
            conversation = new Conversation
            {
                AdId = adId,
                SellerId = sellerId,
                BuyerId = buyerId
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();

            conversation.DialogFolderPath = $"files/{conversation.SellerId}/Ads/{conversation.AdId}/dialogsFolder/{conversation.Id}";
            Directory.CreateDirectory(Path.Combine(env.WebRootPath ?? "wwwroot", conversation.DialogFolderPath));
            await db.SaveChangesAsync();
        }

        // Some clients send the text under the field name "caption" when uploading files.
        var contentText = text;
        if (string.IsNullOrWhiteSpace(contentText) && Request.HasFormContentType)
            contentText = Request.Form["caption"].FirstOrDefault();

        var hasFiles = files is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(contentText) && !hasFiles && replyToMessageId == null)
            return BadRequest(new { error = "Empty message body" });

        if (!hasFiles)
        {
            var message = await writer.EnqueueAsync(conversation.Id, buyerId, MessageType.Text, contentText, replyToMessageId);
            return Ok(new { conversationId = conversation.Id, message });
        }

        var attachments = await Task.WhenAll(files!.Select(f => SaveAttachmentAsync(conversation, f)));
        var type = attachments.All(path => DetectMessageType(Path.GetExtension(path)) == MessageType.Image)
            ? MessageType.Image
            : MessageType.File;

        var messageWithFiles = await writer.EnqueueAsync(conversation.Id, buyerId, type, contentText, replyToMessageId, [.. attachments]);
        return Ok(new { conversationId = conversation.Id, message = messageWithFiles });
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

        var newPaths = await Task.WhenAll(files.Select(f => SaveAttachmentAsync(conv, f)));
        var combined = (existing.Attachments ?? []).Concat(newPaths).ToList();
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

        var urls = await Task.WhenAll(files.Select(f => SaveAttachmentAsync(conv, f)));
        var type = urls.All(u => DetectMessageType(Path.GetExtension(u)) == MessageType.Image) ? MessageType.Image : MessageType.File;
        var msg = await writer.EnqueueAsync(id, userId, type, caption, attachments: [.. urls]);
        return Ok(msg);
    }

    [HttpPost("by-ad/{adId:int}/attachments")]
    public async Task<IActionResult> UploadAttachmentByAd(int adId, [FromForm] List<IFormFile> files, [FromForm] string? caption)
    {
        if (!User.TryGetUserId(out var buyerId)) return Unauthorized();

        var ad = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == adId);
        if (ad == null) return NotFound();

        var sellerId = ad.UserId;
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.AdId == adId && c.SellerId == sellerId && c.BuyerId == buyerId);
        if (conv == null)
        {
            conv = new Conversation { AdId = adId, SellerId = sellerId, BuyerId = buyerId };
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            conv.DialogFolderPath = $"files/{conv.SellerId}/Ads/{conv.AdId}/dialogsFolder/{conv.Id}";
            await db.SaveChangesAsync();
        }

        var urls = await Task.WhenAll(files.Select(f => SaveAttachmentAsync(conv, f)));
        var type = urls.All(u => DetectMessageType(Path.GetExtension(u)) == MessageType.Image) ? MessageType.Image : MessageType.File;
        var msg = await writer.EnqueueAsync(conv.Id, buyerId, type, caption, attachments: [.. urls]);
        return Ok(new { conversationId = conv.Id, message = msg });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetConversation(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var conversation = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);

        if (conversation == null)
        {
            return NotFound(new
            {
                error = "Conversation not found",
                details = new
                {
                    conversationId = id,
                    requestedByUserId = userId,
                    timestamp = DateTime.UtcNow
                }
            });
        }

        if (conversation.SellerId != userId && conversation.BuyerId != userId)
            return Forbid();

        var unread = await writer.GetUnreadStateAsync(conversation, userId);

        return Ok(new
        {
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
            HasUnread = unread.Count > 0,
            UnreadMessagesCount = unread.Count,
            FirstUnreadMessageId = unread.FirstUnreadMessageId,
            UnreadClusterStartMessageId = unread.FirstUnreadMessageId.HasValue ? (int?)Math.Max(unread.FirstUnreadMessageId.Value - 10, 1) : null,
            IsMuted = conversation.SellerId == userId ? conversation.IsMutedForSeller : conversation.IsMutedForBuyer,
            IsArchived = conversation.SellerId == userId ? conversation.IsArchivedForSeller : conversation.IsArchivedForBuyer
        });
    }

    private static MessageType DetectMessageType(string ext) =>
        ext.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" => MessageType.Image,
            _ => MessageType.File
        };

    private async Task<string> SaveAttachmentAsync(Conversation conv, IFormFile file)
    {
        var webRoot = env.WebRootPath ?? "wwwroot";
        var attachFolder = Path.Combine(webRoot, "files", conv.SellerId.ToString(),
            "Ads", conv.AdId.ToString(), "dialogsFolder", conv.Id.ToString(), "attachments");

        var ext = Path.GetExtension(file.FileName);
        var isImage = DetectMessageType(ext) == MessageType.Image;
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

        return $"/files/{conv.SellerId}/Ads/{conv.AdId}/dialogsFolder/{conv.Id}/attachments/{fileName}";
    }
}

public record CreateConversationRequest(int AdId);
public record SendMessageRequest(MessageType Type, string? Text, int? ReplyToMessageId);
public record EditMessageRequest(string? Text, List<string>? Attachments);
