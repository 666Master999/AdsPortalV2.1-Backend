using AdsPortalV2.Entities;

namespace AdsPortalV2.Services;

public interface IConversationRepository
{
    Task<Ad?> GetAdByIdAsync(int adId);
    Task<bool> IsBlockedBidirectionalAsync(int userA, int userB);
    bool IsBlockedBidirectional(int userA, int userB);
    Task<Conversation?> FindByAdAndBuyerAsync(int adId, int buyerId);
    Task AddConversationAsync(Conversation conv);
    Task SaveChangesAsync();
    Task<Conversation?> GetByIdAsync(int id, bool includeDetails = false, bool asNoTracking = false);
    Task<List<Conversation>> GetForUserAsync(int userId);
    Task<Conversation?> GetOrCreateByAdAsync(int adId, int buyerId);
}
