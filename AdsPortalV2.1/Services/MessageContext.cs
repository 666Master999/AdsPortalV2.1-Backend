namespace AdsPortalV2.Services;

public class MessageContext
{
    public int SenderId { get; set; }
    public int ConversationId { get; set; }
    public string? Text { get; set; }

    // Pipeline outcome
    public bool IsRejected { get; set; }
    public string? ErrorCode { get; set; }
}
