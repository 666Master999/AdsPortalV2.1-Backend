using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using AdsPortalV2.Hubs;
using AdsPortalV2.Data;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace AdsPortalV2.Services;

public class MessageFlowService
{
    private readonly IWebHostEnvironment _env;
    private readonly ImageService _imageService;
    private readonly AppDbContext _db;
    private readonly DialogWriterService _writer;
    private readonly IHubContext<ChatHub> _onlineHub;
    private readonly IHubContext<SystemNotificationHub> _notificationHub;
    private readonly ILogger<MessageFlowService> _logger;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"
    };

    private static readonly HashSet<string> AllowedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp",
        // video formats
        ".mp4", ".avi", ".mov", ".mkv", ".webm", ".flv",
        // audio formats
        ".mp3", ".wav", ".aac", ".ogg", ".flac", ".m4a",
        ".pdf", ".txt", ".csv",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".zip", ".rar", ".7z", ".tar", ".gz"
    };

    private readonly ConversationService _conversationService;

    public MessageFlowService(IWebHostEnvironment env, ImageService imageService, ConversationService conversationService)
    {
        _env = env;
        _imageService = imageService;
        _conversationService = conversationService;
    }

    public async Task<ConversationMessageActionDto> CreateAndSendMessageAsync(int conversationId, int userId, MessageType type, string? text, int? replyToMessageId, IEnumerable<ChatAttachment>? attachments = null, string? clientTag = null)
    {
        var list = attachments?.ToList();
        var msg = await _writer.EnqueueAsync(conversationId, userId, type, text, replyToMessageId, list, clientTag);

        var a = await _db.Users.AsNoTracking()
            .Where(u => u.Id == msg.AuthorId)
            .Select(u => new { u.Id, u.UserName, u.UserLogin, u.AvatarPath })
            .FirstOrDefaultAsync();

        MessageAuthorDto authorDto;
        if (a == null) authorDto = new MessageAuthorDto(msg.AuthorId, null, "[deleted]", null);
        else authorDto = new MessageAuthorDto(a.Id, a.UserName, a.UserLogin, a.AvatarPath);

        var convMessage = DialogHelpers.ToConversationMessageDto(conversationId, msg, authorDto);
        var action = new ConversationMessageActionDto(conversationId, convMessage);

        try
        {
            await _notificationHub.Clients.Group($"conversation:{conversationId}").SendAsync(HubEvents.Message, convMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send realtime notification for conversation {ConvId}", conversationId);
        }

        try
        {
            await _onlineHub.Clients.Group($"conversation:{conversationId}").SendAsync(HubEvents.Message, convMessage);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send onlineHub realtime notification for conversation {ConvId}", conversationId);
        }

        return action;
    }

    private static bool IsValidAttachment(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedAttachmentExtensions.Contains(ext))
            return false;

        var contentType = file.ContentType ?? string.Empty;
        return ImageExtensions.Contains(ext)
            ? contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            : !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    public string? ValidateAttachments(IFormFileCollection files)
    {
        foreach (var file in files)
        {
            if (!IsValidAttachment(file))
                return $"Неподдерживаемый тип вложения: {file.FileName}";
        }

        return null;
    }

    private static MessageType DetectMessageType(string ext) =>
        ImageExtensions.Contains(ext) ? MessageType.Image : MessageType.File;

    public async Task<ChatAttachment> SaveAttachmentAsync(Conversation conv, IFormFile file)
    {
        var webRoot = _env.WebRootPath ?? "wwwroot";
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
            await _imageService.SaveCompressedImageAsync(stream, attachFolder, fileName, targetKb: 50);
        }
        else
        {
            await using var output = System.IO.File.Create(fullPath);
            await stream.CopyToAsync(output);
        }

        var url = $"/{conv.DialogFolderPath}/attachments/{fileName}".Replace('\\', '/');
        return new ChatAttachment(url, msgType);
    }

    public async Task<Conversation?> GetOrCreateByAdAsync(AppDbContext db, int adId, int buyerId)
    {
        // Delegate to ConversationService
        return await _conversationService.GetOrCreateByAdAsync(db, adId, buyerId);
    }
}
