using AdsPortalV2.Data;
using AdsPortalV2.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
public class LocationsController(AppDbContext db) : ControllerBase
{
    [HttpGet("regions")]
    public async Task<IActionResult> GetRegions() =>
        Ok(await db.Regions.Select(r => new LocationRef("region", r.Id, r.Name)).ToListAsync());

    [HttpGet("cities")]
    public async Task<IActionResult> GetCities([FromQuery] int? regionId = null)
    {
        var query = db.Cities.AsQueryable();
        if (regionId.HasValue) query = query.Where(c => c.RegionId == regionId.Value);
        return Ok(await query.Select(c => new LocationRef("city", c.Id, c.Name)).ToListAsync());
    }

    [HttpGet("districts")]
    public async Task<IActionResult> GetDistricts([FromQuery] int? cityId = null)
    {
        var query = db.Districts.AsQueryable();
        if (cityId.HasValue) query = query.Where(d => d.CityId == cityId.Value);
        return Ok(await query.Select(d => new LocationRef("district", d.Id, d.Name)).ToListAsync());
    }

    [HttpGet("locations/search")]
    public async Task<IActionResult> Search([FromQuery] string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<object>());

        var term = $"%{q.Trim()}%";
        const int limit = 12;

        var result = await db.Cities
            .Where(x => EF.Functions.Like(x.Name, term))
            .Select(x => new { Sort = 0, Type = "city", x.Id, x.Name })
            .Concat(db.Regions
                .Where(x => EF.Functions.Like(x.Name, term))
                .Select(x => new { Sort = 1, Type = "region", x.Id, x.Name }))
            .Concat(db.Districts
                .Where(x => EF.Functions.Like(x.Name, term))
                .Select(x => new { Sort = 2, Type = "district", x.Id, x.Name }))
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Name)
            .Take(limit)
            .Select(x => new LocationRef(x.Type, x.Id, x.Name))
            .ToListAsync();

        return Ok(result);
    }
}

