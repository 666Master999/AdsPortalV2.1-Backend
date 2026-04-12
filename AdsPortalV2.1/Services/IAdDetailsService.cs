using AdsPortalV2.Models;

namespace AdsPortalV2.Services;

public interface IAdDetailsService
{
    Task<AdDetailsDto?> GetAsync(int adId, int? viewerUserId, bool showModerationStatus = false, CancellationToken cancellationToken = default);
}
