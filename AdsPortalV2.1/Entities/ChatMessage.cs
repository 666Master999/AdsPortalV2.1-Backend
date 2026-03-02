namespace AdsPortalV2.Entities;

public class ChatMessage
{
    public int Id { get; set; }
    public int ChatThreadId { get; set; }
    public int SenderId { get; set; }
    public string Text { get; set; } = "";
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }

    public ChatThread ChatThread { get; set; } = null!;
    public User Sender { get; set; } = null!;
}
