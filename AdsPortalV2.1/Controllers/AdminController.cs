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
    public async Task<IActionResult> GetAds() =>
        Ok(await db.Ads.ToListAsync());

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers() =>
        Ok(await db.Users.ToListAsync());

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs() =>
        Ok(await db.AdminLogs.OrderByDescending(l => l.CreatedAt).ToListAsync());
}
