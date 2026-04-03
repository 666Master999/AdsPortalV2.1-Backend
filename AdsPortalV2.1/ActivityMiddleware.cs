using AdsPortalV2.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace AdsPortalV2;

public class ActivityMiddleware(RequestDelegate next, IMemoryCache cache)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var idValue = context.User.FindFirstValue("id");
            if (int.TryParse(idValue, out var userId))
            {
                var key = $"activity:{userId}";
                if (!cache.TryGetValue(key, out _))
                {
                    cache.Set(key, true, Interval);
                    await db.Users
                        .Where(u => u.Id == userId)
                        .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActivityAt, DateTime.UtcNow));
                }
            }
        }

        await next(context);
    }
}
