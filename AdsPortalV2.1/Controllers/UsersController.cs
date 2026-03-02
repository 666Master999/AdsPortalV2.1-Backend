using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("users")]
public class UsersController(AppDbContext db) : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var user = await db.Users.FindAsync(id);
        return user == null ? NotFound() : Ok(user);
    }

    [Authorize]
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] User updated)
    {
        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();

        user.UserName = updated.UserName;
        user.UserPhoneNumber = updated.UserPhoneNumber;
        user.AvatarPath = updated.AvatarPath;

        await db.SaveChangesAsync();
        return Ok(user);
    }

    [HttpGet("{id}/ads")]
    public async Task<IActionResult> GetAds(int id) =>
        Ok(await db.Ads.Where(a => a.UserId == id && a.IsActive).ToListAsync());

    [HttpGet("{id}/favorites")]
    public async Task<IActionResult> GetFavorites(int id) =>
        Ok(await db.Favorites.Where(f => f.UserId == id).Include(f => f.Ad).ToListAsync());
}
