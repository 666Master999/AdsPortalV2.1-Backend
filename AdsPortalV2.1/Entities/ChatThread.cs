namespace AdsPortalV2.Entities;

public class ChatThread
{
    public int Id { get; set; }
    public int AdId { get; set; }
    public int BuyerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Ad Ad { get; set; } = null!;
    public User Buyer { get; set; } = null!;
    public List<ChatMessage> Messages { get; set; } = [];
}
