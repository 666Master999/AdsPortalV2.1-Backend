using AdsPortalV2.Data;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
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
    public async Task<ActionResult<NotificationsResultDto>> GetAll()
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var notifications = await db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();

        var adIds = notifications
            .Where(n => n.AdId.HasValue)
            .Select(n => n.AdId!.Value)
            .Distinct()
            .ToList();

        Dictionary<int, string?> imagesByAdId = adIds.Count == 0
            ? []
            : await db.Ads
                .AsNoTracking()
                .Where(a => adIds.Contains(a.Id))
                .Select(a => new
                {
                    a.Id,
                    Image = db.AdImages
                        .Where(i => i.Id == a.MainImageId)
                        .Select(i => i.FilePath)
                        .FirstOrDefault()
                })
                .ToDictionaryAsync(x => x.Id, x => x.Image);

        var dto = notifications
            .Select(n => NotificationMapper.ToDto(
                n,
                n.AdId.HasValue && imagesByAdId.TryGetValue(n.AdId.Value, out var image) ? image : null))
            .ToList();
        return Ok(new NotificationsResultDto(dto));
    }

    [HttpPost("read")]
    public async Task<ActionResult> MarkAsRead([FromBody] int[]? ids = null)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();

        var query = db.Notifications.Where(n => n.UserId == userId && !n.IsRead);
        if (ids?.Length > 0)
            query = query.Where(n => ids.Contains(n.Id));

        await query.ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        return Ok();
    }
}
