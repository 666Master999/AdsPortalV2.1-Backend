using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("categories")]
public class CategoriesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await db.Categories.Include(c => c.Children).ToListAsync());

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Category category)
    {
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return Ok(category);
    }

    [Authorize]
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] Category updated)
    {
        var category = await db.Categories.FindAsync(id);
        if (category == null) return NotFound();

        category.Name = updated.Name;
        category.ParentId = updated.ParentId;

        await db.SaveChangesAsync();
        return Ok(category);
    }

    [Authorize]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var category = await db.Categories.FindAsync(id);
        if (category == null) return NotFound();

        db.Categories.Remove(category);
        await db.SaveChangesAsync();
        return Ok();
    }
}
