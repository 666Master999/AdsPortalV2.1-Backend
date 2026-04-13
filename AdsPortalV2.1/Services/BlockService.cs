using AdsPortalV2.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AdsPortalV2.Services;

public class BlockService : IBlockService
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public BlockService(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<bool> CanSendAsync(int userA, int userB)
    {
        var key = $"block:{Math.Min(userA,userB)}:{Math.Max(userA,userB)}";
        if (_cache.TryGetValue(key, out bool blocked))
            return !blocked;

        blocked = await _db.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.SourceUserId == userA && b.TargetUserId == userB) ||
            (b.SourceUserId == userB && b.TargetUserId == userA));

        _cache.Set(key, blocked, TimeSpan.FromSeconds(5));
        return !blocked;
    }

    public async Task<bool> CanSendAsync(int conversationId)
    {
        var conv = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new { c.SellerId, c.BuyerId })
            .FirstOrDefaultAsync();

        if (conv == null) return true;

        return await CanSendAsync(conv.SellerId, conv.BuyerId);
    }
}
