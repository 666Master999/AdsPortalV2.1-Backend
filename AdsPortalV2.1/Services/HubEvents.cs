namespace AdsPortalV2.Services;

public static class HubEvents
{
    // Realtime events (single current contract - no versions)
    public const string Message = "chat:message";
    // Notification intent event: client decides whether to show banner/sound based on IsMuted
    public const string MessageNotificationCandidate = "chat:messageNotificationCandidate";
    public const string ConversationUpdated = "chat:conversationUpdated";
    public const string Read = "chat:read";
    public const string Typing = "chat:typing";
    // NOTE: chat:onlineUsers removed. Use presence events instead.
    // Global presence events
    public const string PresenceInit = "presence:init";
    public const string PresenceOnline = "presence:online";
    public const string PresenceOffline = "presence:offline";
    // Conversation-scoped presence (in-dialog)
    public const string PresenceInDialog = "presence:inDialog";
    public const string PresenceLeftDialog = "presence:leftDialog";
    // Initial dialog state for caller: list of active users in conversation
    public const string PresenceInitDialog = "presence:initDialog";
    public const string InitNotifications = "initNotifications";
    public const string ConversationCreated = "chat:conversationCreated";
}
