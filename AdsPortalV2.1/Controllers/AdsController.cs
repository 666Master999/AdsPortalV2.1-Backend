using System.Security.Claims;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("ads")]
public class AdsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await db.Ads.Where(a => a.IsActive).ToListAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var ad = await db.Ads.Include(a => a.Images).FirstOrDefaultAsync(a => a.Id == id);
        return ad == null ? NotFound() : Ok(ad);
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Ad ad)
    {
        ad.UserId = GetUserId();
        db.Ads.Add(ad);
        await db.SaveChangesAsync();
        return Ok(ad);
    }

    [Authorize]
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] Ad updated)
    {
        var ad = await db.Ads.FindAsync(id);
        if (ad == null) return NotFound();

        ad.Title = updated.Title;
        ad.Description = updated.Description;
        ad.Price = updated.Price;
        ad.CategoryId = updated.CategoryId;
        ad.CityId = updated.CityId;
        ad.IsActive = updated.IsActive;

        await db.SaveChangesAsync();
        return Ok(ad);
    }

    [Authorize]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ad = await db.Ads.FindAsync(id);
        if (ad == null) return NotFound();

        ad.IsActive = false;
        await db.SaveChangesAsync();
        return Ok();
    }

    private int GetUserId() =>
        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}
