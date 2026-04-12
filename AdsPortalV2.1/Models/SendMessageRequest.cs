using AdsPortalV2.Entities;

namespace AdsPortalV2.Models;

public sealed record SendMessageRequest(
    MessageType Type,
    string? Text,
    int? ReplyToMessageId,
    string? ClientTag);
