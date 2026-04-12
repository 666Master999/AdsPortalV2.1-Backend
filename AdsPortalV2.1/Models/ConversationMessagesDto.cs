using System.Collections.Generic;

namespace AdsPortalV2.Models;

public class ConversationMessagesDto
{
    public ConversationDto Conversation { get; set; } = null!;
    public List<ConversationMessageDto> Messages { get; set; } = new();
    public bool HasMore { get; set; }
    public int? AnchorMessageId { get; set; }
    public int? MyLastSeenMessageId { get; set; }
    public int? OtherLastSeenMessageId { get; set; }

    public ConversationMessagesDto(ConversationDto conversation, List<ConversationMessageDto> messages, bool hasMore, int? anchorMessageId, int? myLastSeenMessageId, int? otherLastSeenMessageId)
    {
        Conversation = conversation;
        Messages = messages;
        HasMore = hasMore;
        AnchorMessageId = anchorMessageId;
        MyLastSeenMessageId = myLastSeenMessageId;
        OtherLastSeenMessageId = otherLastSeenMessageId;
    }
}
