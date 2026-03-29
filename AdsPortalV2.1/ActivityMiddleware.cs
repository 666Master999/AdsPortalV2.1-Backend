using AdsPortalV2.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AdsPortalV2;

public class ActivityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var idValue = context.User.FindFirstValue("id");
            if (int.TryParse(idValue, out var userId))
                await db.Users
                    .Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActivityAt, DateTime.UtcNow));
        }

        await next(context);
    }
}
