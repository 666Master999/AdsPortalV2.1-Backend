using AdsPortalV2.Data;
using AdsPortalV2.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("locations")]
public class LocationsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetTree()
    {
        var items = await db.Locations
            .AsNoTracking()
            .OrderBy(l => l.Type)
            .ThenBy(l => l.Name)
            .ToListAsync();

        var nodes = items.ToDictionary(
            x => x.Id,
            x => new LocationTreeNodeDto
            {
                Id = x.Id,
                Name = x.Name,
                Type = x.Type
            });

        List<LocationTreeNodeDto> roots = [];
        foreach (var item in items)
        {
            var node = nodes[item.Id];
            if (item.ParentId is int parentId && nodes.TryGetValue(parentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        return Ok(roots);
    }
}

