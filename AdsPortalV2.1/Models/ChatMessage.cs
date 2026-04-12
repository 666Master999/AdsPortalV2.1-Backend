using AdsPortalV2.Entities;

namespace AdsPortalV2.Models;

public enum DialogMessageEventType
{
    Created,
    Edited,
    Deleted,
    Patched
}

public class ChatMessage
{
    public int Id { get; set; }
    public DialogMessageEventType EventType { get; set; } = DialogMessageEventType.Created;
    public MessageType Type { get; set; }
    public int AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }
    // Optional client-generated id for optimistic UI reconciliation
    public string? ClientTag { get; set; }
    public string? Text { get; set; }
    public List<ChatAttachment>? Attachments { get; set; }
    public int? ReplyToMessageId { get; set; }
    public DateTime? EditedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

// { "url": "/files/...", "type": "image" }
public record ChatAttachment(string Url, MessageType Type);

public class DialogMeta
{
    public int TotalMessages { get; set; }
    public int LastMessageId { get; set; }
    public int LastFileIndex { get; set; }
    public long LastFileSize { get; set; }
    // FileFirstMessageIds[i] = первый id сообщения в messages_{i}.jsonl
    public List<int> FileFirstMessageIds { get; set; } = [];
}

public class DialogSnapshot
{
    public int LastMessageId { get; set; }
    public int LastFileIndex { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
}
