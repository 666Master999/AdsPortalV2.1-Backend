using AdsPortalV2.Controllers;
using AdsPortalV2.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("admin")]
[Authorize]
public class AdminController(AppDbContext db) : ControllerBase
{
    [HttpGet("ads")]
    public async Task<IActionResult> GetAds([FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        if (!User.IsAdmin()) return Forbid();
        take = Math.Clamp(take, 1, 200);
        return Ok(await db.Ads.OrderByDescending(a => a.CreatedAt).Skip(skip).Take(take).ToListAsync());
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers([FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        if (!User.IsAdmin()) return Forbid();
        take = Math.Clamp(take, 1, 200);
        return Ok(await db.Users.OrderByDescending(u => u.CreatedAt).Skip(skip).Take(take).ToListAsync());
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        if (!User.IsAdmin()) return Forbid();
        take = Math.Clamp(take, 1, 200);
        return Ok(await db.AdminLogs.OrderByDescending(l => l.CreatedAt).Skip(skip).Take(take).ToListAsync());
    }
}
