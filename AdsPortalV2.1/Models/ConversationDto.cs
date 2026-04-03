using AdsPortalV2.Entities;

namespace AdsPortalV2.Models;

public sealed record ConversationDto(
    int Id,
    ConversationCompanionDto Companion,
    ConversationAdDto Ad,
    ConversationLastMessageDto? LastMessage,
    int UnreadCount,
    int? FirstUnreadMessageId,
    DateTime? LastMessageAt,
    bool IsClosed,
    bool IsMuted,
    bool IsArchived,
    int TotalMessagesCount);

public sealed record ConversationCompanionDto(
    int Id,
    string Name,
    string? Avatar);

public sealed record ConversationAdDto(
    int Id,
    string Title,
    string? Image,
    string? ModerationStatus);

public sealed record ConversationLastMessageDto(
    int? Id,
    MessageType Type,
    int? AuthorId,
    string? Text,
    DateTime? CreatedAt);

public static class ConversationDtoBuilder
{
    public static ConversationDto ToDto(this Conversation conversation, int currentUserId, int unreadCount, int? firstUnreadMessageId, int? lastMessageId = null)
    {
        var companion = conversation.SellerId == currentUserId ? conversation.Buyer : conversation.Seller;
        var lastMessage = conversation.LastMessageTimestamp == null
            ? null
            : new ConversationLastMessageDto(
                lastMessageId,
                conversation.LastMessageType ?? MessageType.Text,
                conversation.LastMessageAuthorId,
                conversation.LastMessageText,
                conversation.LastMessageTimestamp);

        return new ConversationDto(
            conversation.Id,
            new ConversationCompanionDto(
                companion.Id,
                companion.UserName ?? companion.UserLogin,
                companion.AvatarPath),
            new ConversationAdDto(
                conversation.Ad.Id,
                conversation.Ad.Title,
                conversation.Ad.Images.FirstOrDefault(img => img.IsMain)?.FilePath,
                conversation.Ad.ModerationStatus.ToString()),
            lastMessage,
            unreadCount,
            firstUnreadMessageId,
            lastMessage?.CreatedAt,
            conversation.IsClosed,
            currentUserId == conversation.SellerId ? conversation.IsMutedForSeller : conversation.IsMutedForBuyer,
            currentUserId == conversation.SellerId ? conversation.IsArchivedForSeller : conversation.IsArchivedForBuyer,
            conversation.TotalMessagesCount);
    }
}
