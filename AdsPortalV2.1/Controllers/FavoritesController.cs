using System.Security.Claims;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("favorites")]
[Authorize]
public class FavoritesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await db.Favorites.Where(f => f.UserId == GetUserId()).Include(f => f.Ad).ToListAsync());

    [HttpPost("{adId}")]
    public async Task<IActionResult> Add(int adId)
    {
        var favorite = new Favorite { UserId = GetUserId(), AdId = adId };
        db.Favorites.Add(favorite);
        await db.SaveChangesAsync();
        return Ok(favorite);
    }

    [HttpDelete("{adId}")]
    public async Task<IActionResult> Remove(int adId)
    {
        var userId = GetUserId();
        var favorite = await db.Favorites.FirstOrDefaultAsync(f => f.UserId == userId && f.AdId == adId);
        if (favorite == null) return NotFound();

        db.Favorites.Remove(favorite);
        await db.SaveChangesAsync();
        return Ok();
    }

    private int GetUserId() =>
        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}
