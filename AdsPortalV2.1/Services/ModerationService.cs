using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public interface IModerationService
{
    Task<AdStatusActionDto?> ApproveAd(int id, int actorId, CancellationToken cancellationToken = default);
    Task<AdStatusActionDto?> RejectAd(int id, string reason, int actorId, CancellationToken cancellationToken = default);
}

public class ModerationService : IModerationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ModerationService> _logger;

    public ModerationService(AppDbContext db, ILogger<ModerationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AdStatusActionDto?> ApproveAd(int id, int actorId, CancellationToken cancellationToken = default)
    {
        try { _logger.LogWarning("MODERATION DB CONTEXT ID (ModerationService): {Id}", _db.ContextId.InstanceId); } catch {}
        var ad = await _db.Ads.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (ad == null) return null;

        var actorName = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == actorId)
            .Select(u => u.UserName ?? u.UserLogin)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        ad.Approve(actorId, actorName);

        _logger.LogInformation("Saving changes for ApproveAd AdId={AdId}", ad.Id);
        await _db.SaveChangesAsync(cancellationToken);
        return new AdStatusActionDto(id, (AdStatus)ad.Status);
    }

    public async Task<AdStatusActionDto?> RejectAd(int id, string reason, int actorId, CancellationToken cancellationToken = default)
    {
        try { _logger.LogWarning("MODERATION DB CONTEXT ID (ModerationService): {Id}", _db.ContextId.InstanceId); } catch {}
        var ad = await _db.Ads.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (ad == null) return null;

        var actorName = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == actorId)
            .Select(u => u.UserName ?? u.UserLogin)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        ad.Reject(actorId, reason, actorName);

        _logger.LogInformation("Saving changes for RejectAd AdId={AdId}", ad.Id);
        await _db.SaveChangesAsync(cancellationToken);
        return new AdStatusActionDto(id, (AdStatus)ad.Status);
    }
}
