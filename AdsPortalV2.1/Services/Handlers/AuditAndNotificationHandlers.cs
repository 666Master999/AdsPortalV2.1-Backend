using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services.Handlers;

public class AuditHandler : IDomainEventHandler<AdApproved>, IDomainEventHandler<AdRejected>
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<AuditHandler> _logger;

    public AuditHandler(IServiceProvider provider, ILogger<AuditHandler> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task Handle(AdApproved @event, long outboxMessageId)
    {
        try
        {
            _logger.LogWarning("AuditHandler.Handle AdApproved called for AdId={AdId} Outbox={Outbox}", @event.AdId, outboxMessageId);
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ad = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == @event.AdId);
            if (ad == null) return;

            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = @event.ActorId,
                TargetUserId = ad.UserId,
                Action = "ad.approve",
                TargetType = "Ad",
                TargetId = ad.Id,
                OldValue = @event.OldValue,
                NewValue = @event.NewValue,
                OutboxMessageId = outboxMessageId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AuditHandler AdApproved failed for {AdId}", @event.AdId);
        }
    }

    public async Task Handle(AdRejected @event, long outboxMessageId)
    {
        try
        {
            _logger.LogWarning("AuditHandler.Handle AdRejected called for AdId={AdId} Outbox={Outbox}", @event.AdId, outboxMessageId);
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ad = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == @event.AdId);
            if (ad == null) return;

            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = @event.ActorId,
                TargetUserId = ad.UserId,
                Action = "ad.reject",
                TargetType = "Ad",
                TargetId = ad.Id,
                Reason = @event.Reason,
                OldValue = @event.OldValue,
                NewValue = @event.NewValue,
                OutboxMessageId = outboxMessageId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AuditHandler AdRejected failed for {AdId}", @event.AdId);
        }
    }
}

public class NotificationHandler : IDomainEventHandler<AdApproved>, IDomainEventHandler<AdRejected>
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<NotificationHandler> _logger;

    public NotificationHandler(IServiceProvider provider, ILogger<NotificationHandler> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task Handle(AdApproved @event, long outboxMessageId)
    {
        try
        {
            _logger.LogWarning("NotificationHandler.Handle AdApproved called for AdId={AdId} Outbox={Outbox}", @event.AdId, outboxMessageId);
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var factory = scope.ServiceProvider.GetRequiredService<INotificationFactory>();
            var svc = scope.ServiceProvider.GetRequiredService<INotificationService>();

            var ad = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == @event.AdId);
            if (ad == null) return;

            var notification = factory.CreateAdApproved(ad.UserId, ad.Id, ad.Title, @event.ActorName ?? string.Empty);
            notification.OutboxMessageId = outboxMessageId;
            _logger.LogWarning("NotificationHandler: created notification for User={UserId} Ad={AdId} Outbox={Outbox}", notification.UserId, notification.AdId, notification.OutboxMessageId);
            await svc.SendAsync(notification);
            _logger.LogWarning("NotificationHandler: SendAsync completed for Outbox={Outbox}", outboxMessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationHandler AdApproved failed for {AdId}", @event.AdId);
        }
    }

    public async Task Handle(AdRejected @event, long outboxMessageId)
    {
        try
        {
            _logger.LogWarning("NotificationHandler.Handle AdRejected called for AdId={AdId} Outbox={Outbox}", @event.AdId, outboxMessageId);
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var factory = scope.ServiceProvider.GetRequiredService<INotificationFactory>();
            var svc = scope.ServiceProvider.GetRequiredService<INotificationService>();

            var ad = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == @event.AdId);
            if (ad == null) return;

            var notification = factory.CreateAdRejected(ad.UserId, ad.Id, ad.Title, @event.Reason ?? string.Empty, @event.ActorName ?? string.Empty);
            notification.OutboxMessageId = outboxMessageId;
            _logger.LogWarning("NotificationHandler: created notification for User={UserId} Ad={AdId} Outbox={Outbox}", notification.UserId, notification.AdId, notification.OutboxMessageId);
            await svc.SendAsync(notification);
            _logger.LogWarning("NotificationHandler: SendAsync completed for Outbox={Outbox}", outboxMessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationHandler AdRejected failed for {AdId}", @event.AdId);
        }
    }
}

public class FileDeletionHandler : IDomainEventHandler<FileDeletionRequested>
{
    private readonly IFileStorage _fileStorage;
    private readonly ILogger<FileDeletionHandler> _logger;

    public FileDeletionHandler(IFileStorage fileStorage, ILogger<FileDeletionHandler> logger)
    {
        _fileStorage = fileStorage;
        _logger = logger;
    }

    public Task Handle(FileDeletionRequested @event, long outboxMessageId)
    {
        _logger.LogWarning("FileDeletionHandler.Handle called for Outbox={Outbox} Paths={Count}", outboxMessageId, @event.FilePaths?.Length ?? 0);

        foreach (var path in (@event.FilePaths ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _fileStorage.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File delete failed: {Path}", path);
            }
        }

        return Task.CompletedTask;
    }
}
