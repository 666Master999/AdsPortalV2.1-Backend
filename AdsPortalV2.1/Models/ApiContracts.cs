using AdsPortalV2.Entities;
using System.Text.Json.Serialization;

namespace AdsPortalV2.Models;

public sealed record PagedResultDto<T>(IReadOnlyCollection<T> Items, int Total, int Page, int PageSize, int TotalPages, string? NextCursor = null, bool HasMore = false);

public sealed record AdImageDto(int Id, int AdId, string FilePath, int SortOrder, bool IsMain = false);
public sealed record AdCategoryDto(int Id, string Name, int? ParentId = null);
public sealed record AdOwnerDto(int Id, string UserLogin, string? UserName, string? UserEmail, string? UserPhoneNumber, string? AvatarPath, IReadOnlyCollection<string> Roles, DateTime CreatedAt, DateTime LastActivityAt);
public sealed record AdDetailsDto(
    int Id,
    int UserId,
    int? CategoryId,
    string Title,
    string? Description,
    decimal? Price,
    bool IsNegotiable,
    int LocationId,
    LocationRef? Location,
    string? ListingType,
    int? MainImageId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    AdStatus? ModerationStatus,
    string? RejectionReason,
    DateTime? DeletedAt,
    AdCategoryDto? Category,
    AdOwnerDto? User,
    IReadOnlyCollection<AdImageDto> Images,
    IReadOnlyCollection<AdAttributeValueDto> AttributeValues,
    bool IsFavorite);

public sealed record AdAttributeFilterDto(IReadOnlyCollection<int> AttributeIds, IReadOnlyCollection<string> Values);

public sealed record ModerationAdDto(
    int Id,
    string Title,
    string? Description,
    decimal? Price,
    int? CategoryId,
    int LocationId,
    LocationRef? Location,
    string? ListingType,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int UserId,
    string? UserName,
    string? UserLogin,
    string? MainImagePath);

public sealed record CreateAdResultDto(string Message, int AdId, IReadOnlyCollection<AdImageDto>? Images = null);
public sealed record UploadFilesResultDto(IReadOnlyCollection<string> Files);
public record PatchIssueDto(string Code, string? Field, string Message);
public sealed record PatchResultDto(bool Success, IReadOnlyCollection<string> Updated, IReadOnlyCollection<PatchIssueDto> Skipped, IReadOnlyCollection<PatchIssueDto> Errors);
public sealed record FavoriteMutationDto(int AdId, DateTime? AddedAt = null);
public sealed record AvatarUploadDto(string AvatarPath);
public sealed record RestrictionActionDto(
    int Id,
    string Restriction,
    string? Reason,
    DateTime? ExpiresAt,
    bool RevokeSessions);

public sealed record RestrictionTypeDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label);
public sealed record RoleActionDto(int Id, string Role);
public sealed record AdStatusActionDto(int Id, AdStatus Status);
public sealed record NotificationPreviewDto(string Title, string? MainImagePath = null);
public sealed record NotificationDataDto(string ActorName, string? Reason = null);

public sealed record NotificationDto(
    int Id,
    string Type,
    bool IsRead,
    DateTime CreatedAt,
    int? AdId,
    string? AdTitle,
    string? Reason,
    string? MainImagePath,
    string? ActorName);

public sealed record NotificationsResultDto(IReadOnlyCollection<NotificationDto> Items);

// Notification intent event sent over realtime channel. Client decides presentation based on IsMuted flag.
public sealed record MessageNotificationCandidateEvent(
    int ConversationId,
    int MessageId,
    int SenderId,
    DateTime CreatedAt,
    bool IsMuted);

public sealed record UserAdDto(
    int Id,
    string Title,
    string? Description,
    decimal? Price,
    int LocationId,
    LocationRef? Location,
    string? ListingType,
    bool IsNegotiable,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int ViewsCount,
    int FavoritesCount,
    AdStatus? ModerationStatus,
    string? RejectionReason,
    DateTime? DeletedAt,
    int UserId,
    AdCategoryDto? Category,
    string? MainImagePath,
    bool IsFavorite = false);

public sealed record UserProfileDto(
    int Id,
    string UserLogin,
    string? UserName,
    string? UserEmail,
    string? UserPhoneNumber,
    string? AvatarPath,
    IReadOnlyCollection<string> Roles,
    bool IsOnline,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    IReadOnlyCollection<UserAdDto> Ads);

public sealed record UserProfileResponseDto(UserProfileDto UserProfile, IReadOnlyCollection<int>? CurrentUserFavorites);

public sealed record FavoriteAdDto(
    int Id,
    string Title,
    decimal? Price,
    bool IsNegotiable,
    string? MainImagePath,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    LocationRef? Location,
    int ViewsCount,
    int FavoritesCount,
    AdStatus Status,
    bool IsFavorite,
    int UserId);

public sealed record ConversationActionDto(int ConversationId, string Message, ConversationDto? Conversation = null);
public sealed record ConversationMessageActionDto(int ConversationId, ConversationMessageDto Message);

public sealed record ConversationAdMetaDto(int Id, string Title, decimal? Price, DateTime CreatedAt, AdStatus Status, string? MainImagePath);
public sealed record ConversationUserPresenceDto(int Id, string? UserName, string UserLogin, string? AvatarPath, DateTime LastActivityAt, bool IsOnline);
public sealed record ConversationMetaDto(int Id, int AdId, ConversationAdMetaDto Ad, ConversationUserPresenceDto Me, ConversationUserPresenceDto Opponent, DateTime CreatedAt, bool IsClosed, string? LastMessageText);

public sealed record MessageAuthorDto(int Id, string? UserName, string UserLogin, string? AvatarPath);
public sealed record ReplyPreviewDto(int Id, int AuthorId, string? AuthorName, string? TextSnippet);

public sealed record ChatAttachmentDto(string Url, MessageType Type, string? FileName = null, string? MimeType = null, long? Size = null, string? ThumbnailUrl = null);

public sealed record ConversationMessageDto(
    int ConversationId,
    int Id,
    MessageType Type,
    int AuthorId,
    MessageAuthorDto Author,
    DateTime CreatedAt,
    string? Text,
    IReadOnlyCollection<ChatAttachmentDto> Attachments,
    int? ReplyToMessageId,
    ReplyPreviewDto? ReplyPreview,
    DateTime? EditedAt,
    DateTime? DeletedAt,
    string? ClientTag,
    bool IsRead);

public sealed record ConversationMessagesChunkDto(IReadOnlyCollection<ConversationMessageDto> Messages, bool HasMore);

public sealed record ConversationInitialDto(
    ConversationMetaDto Conversation,
    IReadOnlyCollection<ConversationMessageDto> Messages,
    bool HasMore,
    int? AnchorMessageId,
    int? MyLastSeenMessageId,
    int? OtherLastSeenMessageId);

public sealed record ConversationStateDto(
    int Id,
    int AdId,
    int SellerId,
    int BuyerId,
    DateTime CreatedAt,
    bool IsClosed,
    DateTime? LastMessageTimestamp,
    MessageType? LastMessageType,
    string? LastMessageText,
    int? LastMessageAuthorId,
    int TotalMessagesCount,
    bool HasUnread,
    int UnreadCount,
    int? MyLastSeenMessageId,
    int? OtherLastSeenMessageId,
    bool IsMuted,
    bool IsArchived);

public sealed record AdminAuditLogDto(
    int Id,
    int ActorUserId,
    int? TargetUserId,
    string Action,
    string? TargetType,
    int? TargetId,
    string? Reason,
    string? OldValue,
    string? NewValue,
    DateTime Timestamp);

public sealed record AdminAdOwnerDto(int Id, string UserLogin, string? UserName, string? AvatarPath);

public sealed record AdminAdListItemDto(
    int Id,
    int UserId,
    int? CategoryId,
    string Title,
    string? Description,
    decimal? Price,
    string? ListingType,
    bool IsNegotiable,
    int LocationId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    AdStatus ModerationStatus,
    string? RejectionReason,
    DateTime? DeletedAt,
    int ViewsCount,
    int FavoritesCount,
    AdminAdOwnerDto? Owner,
    AdCategoryDto? Category,
    LocationRef? Location,
    string? MainImagePath);

public sealed record AuthSessionResponseDto(string AccessToken, string RefreshToken, int UserId, string UserLogin, string? UserName, string? AvatarPath);
public sealed record AuthRefreshResponseDto(string AccessToken, string RefreshToken);
public sealed record AuthSessionDto(Guid Id, string? DeviceName, string? IpAddress, DateTime LastActivityAt, DateTime CreatedAt, bool IsCurrent);
public sealed record MeRestrictionDto(string Type, DateTime? ExpiresAt, string? Reason);
