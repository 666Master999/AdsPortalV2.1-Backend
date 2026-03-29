using AdsPortalV2.Entities;

namespace AdsPortalV2.Models;

public class ChatMessage
{
    public int Id { get; set; }
    public MessageType Type { get; set; }
    public int AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Text { get; set; }
    public List<string>? Attachments { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public int? ReplyToMessageId { get; set; }
    public DateTime? EditedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool Edited => EditedAt.HasValue;
    public bool Deleted => DeletedAt.HasValue;
}

public class ClusterMeta
{
    public int MessageCount { get; set; }
    public int FirstMessageId { get; set; }
    public int LastMessageId { get; set; }
}

public class DialogMeta
{
    public int TotalClusters { get; set; }
    public int TotalMessages { get; set; }
    public long LastClusterSize { get; set; }
    public int LastMessageId { get; set; }
}
