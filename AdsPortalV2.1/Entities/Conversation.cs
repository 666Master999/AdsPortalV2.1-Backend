namespace AdsPortalV2.Entities;

public class Conversation
{
    public int Id { get; set; }
    public int AdId { get; set; }
    public int SellerId { get; set; }
    public int BuyerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsClosed { get; set; }
    public DateTime? LastMessageTimestamp { get; set; }
    public string DialogFolderPath { get; set; } = string.Empty;

    public bool HasUnreadForSeller { get; set; }
    public bool HasUnreadForBuyer { get; set; }

    public MessageType? LastMessageType { get; set; }
    public string? LastMessageText { get; set; }
    public int TotalMessagesCount { get; set; }
    public int? LastMessageAuthorId { get; set; }

    public bool IsMutedForSeller { get; set; }
    public bool IsMutedForBuyer { get; set; }
    public bool IsArchivedForSeller { get; set; }
    public bool IsArchivedForBuyer { get; set; }

    public int LastClusterId { get; set; }

    public Ad Ad { get; set; } = null!;
    public User Seller { get; set; } = null!;
    public User Buyer { get; set; } = null!;


    public int? SellerDeletedUpToMessageId { get; set; }
    public int? BuyerDeletedUpToMessageId { get; set; }
}
