using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("notifications")]
[Authorize]
public class NotificationsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var notifications = await db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new
            {
                n.Id,
                n.Type,
                n.AdId,
                n.Message,
                n.IsRead,
                n.CreatedAt
            })
            .ToListAsync();

        return Ok(notifications);
    }

    [HttpPost("read")]
    public async Task<IActionResult> MarkAsRead([FromBody] int[]? ids = null)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var query = db.Notifications.Where(n => n.UserId == userId && !n.IsRead);
        if (ids?.Length > 0)
            query = query.Where(n => ids.Contains(n.Id));

        await query.ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        return Ok();
    }
}
