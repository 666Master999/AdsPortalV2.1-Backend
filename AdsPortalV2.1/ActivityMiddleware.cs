using AdsPortalV2.Data;
using AdsPortalV2.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace AdsPortalV2;

public class ActivityMiddleware(RequestDelegate next, IMemoryCache cache)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true && context.User.TryGetUserId(out var userId))
        {
            var key = $"activity:{userId}";
            if (!cache.TryGetValue(key, out _))
            {
                cache.Set(key, true, Interval);
                var now = DateTime.UtcNow;

                await db.Users
                    .Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActivityAt, now));

                var sidValue = context.User.FindFirstValue("sid");
                if (Guid.TryParse(sidValue, out var sessionId))
                    await db.AuthSessions
                        .Where(s => s.Id == sessionId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActivityAt, now));
            }
        }

        await next(context);
    }
}
