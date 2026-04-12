using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using System.IO;

namespace AdsPortalV2.Services;

public class ConversationService
{
    private readonly IWebHostEnvironment _env;

    public ConversationService(IWebHostEnvironment env)
    {
        _env = env;
    }

    public async Task<Conversation?> GetOrCreateByAdAsync(AppDbContext db, int adId, int buyerId)
    {
        var sellerId = await db.Ads.AsNoTracking().Where(a => a.Id == adId).Select(a => (int?)a.UserId).FirstOrDefaultAsync();
        if (sellerId == null) return null;

        var blocked = await db.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.SourceUserId == buyerId && b.TargetUserId == sellerId.Value) ||
            (b.SourceUserId == sellerId.Value && b.TargetUserId == buyerId));
        if (blocked) return null;

        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.AdId == adId && c.SellerId == sellerId && c.BuyerId == buyerId);
        if (conv != null) return conv;

        conv = new Conversation
        {
            AdId = adId,
            SellerId = sellerId.Value,
            BuyerId = buyerId
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        conv.DialogFolderPath = $"files/{sellerId.Value}/Ads/{adId}/dialogs/{conv.Id}";
        await db.SaveChangesAsync();
        // Delegate file system work to IFileStorage implementation via DI if available.
        // Resolve IFileStorage from the ambient IServiceProvider to avoid changing method signature.
        try
        {
            var sp = (IServiceProvider?)_env.GetType().GetProperty("ApplicationServices")?.GetValue(_env) ?? null;
            var fs = sp?.GetService(typeof(IFileStorage)) as IFileStorage;
            if (fs != null)
                await fs.CreateConversationFoldersAsync(conv);
            else
            {
                Directory.CreateDirectory(Path.Combine(_env.WebRootPath ?? "wwwroot", conv.DialogFolderPath));
                Directory.CreateDirectory(Path.Combine(_env.WebRootPath ?? "wwwroot", conv.DialogFolderPath, "attachments"));
            }
        }
        catch
        {
            // fallback to direct file ops if DI not available or fails
            Directory.CreateDirectory(Path.Combine(_env.WebRootPath ?? "wwwroot", conv.DialogFolderPath));
            Directory.CreateDirectory(Path.Combine(_env.WebRootPath ?? "wwwroot", conv.DialogFolderPath, "attachments"));
        }
        return conv;
    }
}
