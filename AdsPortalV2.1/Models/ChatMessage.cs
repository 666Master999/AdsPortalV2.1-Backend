using AdsPortalV2.Entities;

namespace AdsPortalV2.Models;

public class ChatMessage
{
    public int Id { get; set; }
    public MessageType Type { get; set; }
    public int AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Text { get; set; }
    public List<ChatAttachment>? Attachments { get; set; }
    public int? ReplyToMessageId { get; set; }
    public DateTime? EditedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

// { "url": "/files/...", "type": "Image" }
public record ChatAttachment(string Url, string Type);

public class DialogMeta
{
    public int TotalMessages { get; set; }
    public int LastMessageId { get; set; }
    public int LastFileIndex { get; set; }
    public long LastFileSize { get; set; }
    // FileFirstMessageIds[i] = первый id сообщения в messages_{i}.jsonl
    public List<int> FileFirstMessageIds { get; set; } = [];
}
