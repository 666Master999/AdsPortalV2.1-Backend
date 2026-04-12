using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("categories")]
public class CategoriesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<CategoryDto>>> GetAll() =>
        Ok(await db.Categories
            .AsNoTracking()
            .Select(c => new CategoryDto(c.Id, c.Name, c.ParentId))
            .ToListAsync());

    [Authorize]
    [Authorize(Policy = "CanManageCategories")]
    [HttpPost]
    public async Task<ActionResult<CategoryDto>> Create([FromBody] UpsertCategoryDto request)
    {
        var category = new Category { Name = request.Name, ParentId = request.ParentId };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return Ok(new CategoryDto(category.Id, category.Name, category.ParentId));
    }

    [Authorize]
    [Authorize(Policy = "CanManageCategories")]
    [HttpPut("{id}")]
    public async Task<ActionResult<CategoryDto>> Update(int id, [FromBody] UpsertCategoryDto updated)
    {
        var category = await db.Categories.FindAsync(id);
        if (category == null) return NotFound();

        category.Name = updated.Name;
        category.ParentId = updated.ParentId;

        await db.SaveChangesAsync();
        return Ok(new CategoryDto(category.Id, category.Name, category.ParentId));
    }

    [Authorize]
    [Authorize(Policy = "CanManageCategories")]
    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var category = await db.Categories.FindAsync(id);
        if (category == null) return NotFound();

        db.Categories.Remove(category);
        await db.SaveChangesAsync();
        return Ok();
    }
}
