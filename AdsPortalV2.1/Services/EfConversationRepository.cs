using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public class EfConversationRepository : IConversationRepository
{
    private readonly AppDbContext _db;

    public EfConversationRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<Ad?> GetAdByIdAsync(int adId) =>
        _db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == adId);

    public Task<bool> IsBlockedBidirectionalAsync(int userA, int userB) =>
        _db.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.SourceUserId == userA && b.TargetUserId == userB) ||
            (b.SourceUserId == userB && b.TargetUserId == userA));

    public bool IsBlockedBidirectional(int userA, int userB) =>
        _db.UserBlocks.AsNoTracking().Any(b =>
            (b.SourceUserId == userA && b.TargetUserId == userB) ||
            (b.SourceUserId == userB && b.TargetUserId == userA));

    public Task<Conversation?> FindByAdAndBuyerAsync(int adId, int buyerId) =>
        _db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.AdId == adId && c.BuyerId == buyerId);

    public Task AddConversationAsync(Conversation conv)
    {
        _db.Conversations.Add(conv);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync() => _db.SaveChangesAsync();

    public Task<Conversation?> GetByIdAsync(int id, bool includeDetails = false, bool asNoTracking = false)
    {
        var q = _db.Conversations.AsQueryable();
        if (asNoTracking) q = q.AsNoTracking();
        if (includeDetails)
            q = q.Include(c => c.Ad).ThenInclude(a => a.Images).Include(c => c.Seller).Include(c => c.Buyer);
        return q.FirstOrDefaultAsync(c => c.Id == id);
    }

    public Task<List<Conversation>> GetForUserAsync(int userId) =>
        _db.Conversations
            .AsNoTracking()
            .Where(c => c.SellerId == userId || c.BuyerId == userId)
            .Include(c => c.Ad).ThenInclude(a => a.Images)
            .Include(c => c.Seller)
            .Include(c => c.Buyer)
            .OrderByDescending(c => c.LastMessageTimestamp)
            .ToListAsync();

    public Task<Conversation?> GetOrCreateByAdAsync(int adId, int buyerId)
    {
        // delegate to ConversationService for folder creation etc.
        throw new NotImplementedException();
    }
}
