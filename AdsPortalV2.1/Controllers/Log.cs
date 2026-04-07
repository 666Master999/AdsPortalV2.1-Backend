using Microsoft.Extensions.Logging;
using AdsPortalV2.Entities;

namespace AdsPortalV2.Controllers;

public static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Ad moderation: id={AdId} {From} -> {To}")]
    public static partial void AdModeration(ILogger logger, int adId, AdStatus from, AdStatus to);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Ad created: id={AdId} by user={UserId}")]
    public static partial void AdCreated(ILogger logger, int adId, int userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Ad deleted: id={AdId} by user={UserId}")]
    public static partial void AdDeleted(ILogger logger, int adId, int userId);
}
