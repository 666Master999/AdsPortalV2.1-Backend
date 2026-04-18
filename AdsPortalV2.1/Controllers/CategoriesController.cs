using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("categories")]
public class CategoriesController(AppDbContext db, ICategoryService categories) : ControllerBase
{
        // GET /categories/attribute-lookup?ids=1,2&onlyFilters=true
        [HttpGet("attribute-lookup")]
        public async Task<ActionResult<IReadOnlyDictionary<string, IReadOnlyCollection<CategoryAttributeDto>>>> GetAttributeLookup([FromQuery] string? ids, [FromQuery] bool onlyFilters = false)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return BadRequest(new ApiError("validation_error", "Parameter 'ids' is required. Provide comma-separated category ids."));

            var parts = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = new List<int>();
            foreach (var p in parts)
            {
                if (int.TryParse(p, out var id)) parsed.Add(id);
            }

            if (parsed.Count == 0)
                return BadRequest(new ApiError("validation_error", "No valid category ids provided."));

            var lookup = await categories.GetAttributeLookupAsync(parsed, onlyFilters);
            return Ok(lookup);
        }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<CategoryDto>>> GetAll()
    {
        var items = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Path)
            .ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.ParentId, c.IsLeaf, c.Path))
            .ToListAsync();

        return Ok(items);
    }

    [HttpGet("tree")]
    public async Task<ActionResult<IReadOnlyCollection<CategoryTreeDto>>> GetTree() =>
        Ok(await categories.GetTreeAsync());

    [HttpGet("{id:int}/attributes")]
    public async Task<ActionResult<IReadOnlyCollection<CategoryAttributeDto>>> GetAttributes(int id)
    {
        var categoryExists = await db.Categories.AnyAsync(c => c.Id == id);
        if (!categoryExists)
            return NotFound();

        return Ok(await categories.GetAttributesAsync(id));
    }

    [HttpGet("{id:int}/filters")]
    public async Task<ActionResult<IReadOnlyCollection<CategoryAttributeDto>>> GetFilters(int id)
    {
        var categoryExists = await db.Categories.AnyAsync(c => c.Id == id);
        if (!categoryExists)
            return NotFound();

        return Ok(await categories.GetAttributesAsync(id, onlyFilters: true));
    }

    [HttpGet("{id:int}/view")]
    public async Task<ActionResult<CategoryViewDto>> GetView(int id)
    {
        var view = await categories.GetViewAsync(id);
        return view == null ? NotFound() : Ok(view);
    }

    [Authorize(Policy = "CanManageCategories")]
    [HttpPost]
    public async Task<ActionResult<CategoryDto>> Create([FromBody] UpsertCategoryDto request)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new ApiError("validation_error", "Category name is required."));

        if (request.ParentId.HasValue)
        {
            var parentExists = await db.Categories.AnyAsync(c => c.Id == request.ParentId.Value);
            if (!parentExists)
                return BadRequest(new ApiError("validation_error", "Parent category was not found."));
        }

        var duplicate = await db.Categories.AnyAsync(c => c.ParentId == request.ParentId && c.Name == name);
        if (duplicate)
            return Conflict(new ApiError("validation_error", "Category with the same name already exists at this level."));

        var category = new Category { Name = name, ParentId = request.ParentId };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        await categories.RebuildAsync();

        return Ok(new CategoryDto(category.Id, category.Name, category.ParentId, category.IsLeaf, category.Path));
    }

    [Authorize(Policy = "CanManageCategories")]
    [HttpPut("{id:int}")]
    public async Task<ActionResult<CategoryDto>> Update(int id, [FromBody] UpsertCategoryDto updated)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id);
        if (category == null)
            return NotFound();

        var name = updated.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new ApiError("validation_error", "Category name is required."));

        if (updated.ParentId == id)
            return BadRequest(new ApiError("validation_error", "A category cannot be its own parent."));

        if (updated.ParentId.HasValue)
        {
            var parentPath = await categories.GetPathIdsAsync(updated.ParentId.Value);
            if (parentPath.Contains(id))
                return BadRequest(new ApiError("validation_error", "A category cannot be moved under its own descendant."));
        }

        if (updated.ParentId.HasValue)
        {
            var parentExists = await db.Categories.AnyAsync(c => c.Id == updated.ParentId.Value);
            if (!parentExists)
                return BadRequest(new ApiError("validation_error", "Parent category was not found."));
        }

        var duplicate = await db.Categories.AnyAsync(c => c.ParentId == updated.ParentId && c.Name == name && c.Id != id);
        if (duplicate)
            return Conflict(new ApiError("validation_error", "Category with the same name already exists at this level."));

        category.Name = name;
        category.ParentId = updated.ParentId;

        await db.SaveChangesAsync();
        await categories.RebuildAsync();

        return Ok(new CategoryDto(category.Id, category.Name, category.ParentId, category.IsLeaf, category.Path));
    }

    [Authorize(Policy = "CanManageCategories")]
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var category = await db.Categories
            .Include(c => c.Children)
            .Include(c => c.Ads)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
            return NotFound();

        if (category.Children.Count > 0)
            return Conflict(new ApiError("validation_error", "Category with children cannot be deleted."));

        if (category.Ads.Count > 0)
            return Conflict(new ApiError("validation_error", "Category that is used by ads cannot be deleted."));

        db.Categories.Remove(category);
        await db.SaveChangesAsync();
        await categories.RebuildAsync();
        return Ok();
    }

    [Authorize(Policy = "CanManageCategories")]
    [HttpPost("{id:int}/attributes")]
    public async Task<ActionResult<CategoryAttributeDto>> CreateAttribute(int id, [FromBody] UpsertCategoryAttributeDto request)
    {
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (category == null)
            return NotFound();

        if (!category.IsLeaf)
            return Conflict(new ApiError("validation_error", "Attributes can be assigned only to leaf categories."));

        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new ApiError("validation_error", "Attribute name is required."));

        var slug = NormalizeSlug(request.Slug);
        if (string.IsNullOrWhiteSpace(slug))
            return BadRequest(new ApiError("validation_error", "Attribute slug is required."));

        if (request.Type == AttributeType.Enum && (request.Options == null || request.Options.Count == 0))
            return BadRequest(new ApiError("validation_error", "Enum attributes must define options."));

        var existing = await db.CategoryAttributes.AnyAsync(a => a.CategoryId == id && a.Slug == slug);
        if (existing)
            return Conflict(new ApiError("validation_error", "Attribute with the same slug already exists in this category."));

        var attribute = new CategoryAttribute
        {
            CategoryId = id,
            Slug = slug,
            Name = name,
            Type = request.Type,
            IsRequired = request.IsRequired,
            IsFilter = request.IsFilter
        };

        if (request.Type == AttributeType.Enum)
        {
            foreach (var option in NormalizeOptions(request.Options!))
                attribute.Options.Add(new CategoryAttributeOption { Value = option });
        }

        db.CategoryAttributes.Add(attribute);
        await db.SaveChangesAsync();
        await categories.RebuildAsync();

        var created = (await categories.GetAttributesAsync(id)).FirstOrDefault(a => a.Slug == slug);
        return created == null ? NotFound() : Ok(created);
    }

    [Authorize(Policy = "CanManageCategories")]
    [HttpPut("attributes/{attributeId:int}")]
    public async Task<ActionResult<CategoryAttributeDto>> UpdateAttribute(int attributeId, [FromBody] UpsertCategoryAttributeDto request)
    {
        var attribute = await db.CategoryAttributes.Include(a => a.Options).FirstOrDefaultAsync(a => a.Id == attributeId);
        if (attribute == null)
            return NotFound();

        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new ApiError("validation_error", "Attribute name is required."));

        var slug = NormalizeSlug(request.Slug);
        if (string.IsNullOrWhiteSpace(slug))
            return BadRequest(new ApiError("validation_error", "Attribute slug is required."));

        if (request.Type == AttributeType.Enum && (request.Options == null || request.Options.Count == 0))
            return BadRequest(new ApiError("validation_error", "Enum attributes must define options."));

        var duplicate = await db.CategoryAttributes.AnyAsync(a => a.CategoryId == attribute.CategoryId && a.Id != attributeId && a.Slug == slug);
        if (duplicate)
            return Conflict(new ApiError("validation_error", "Attribute with the same slug already exists in this category."));

        attribute.Slug = slug;
        attribute.Name = name;
        attribute.Type = request.Type;
        attribute.IsRequired = request.IsRequired;
        attribute.IsFilter = request.IsFilter;

        db.CategoryAttributeOptions.RemoveRange(attribute.Options);
        attribute.Options.Clear();

        if (request.Type == AttributeType.Enum)
        {
            foreach (var option in NormalizeOptions(request.Options!))
                attribute.Options.Add(new CategoryAttributeOption { Value = option });
        }

        await db.SaveChangesAsync();
        await categories.RebuildAsync();

        var updatedAttribute = (await categories.GetAttributesAsync(attribute.CategoryId)).FirstOrDefault(a => a.Slug == slug);
        return updatedAttribute == null ? NotFound() : Ok(updatedAttribute);
    }

    [Authorize(Policy = "CanManageCategories")]
    [HttpDelete("attributes/{attributeId:int}")]
    public async Task<ActionResult> DeleteAttribute(int attributeId)
    {
        var attribute = await db.CategoryAttributes.FirstOrDefaultAsync(a => a.Id == attributeId);
        if (attribute == null)
            return NotFound();

        var hasValues = await db.AdAttributeValues.AnyAsync(v => v.AttributeId == attributeId);
        if (hasValues)
            return Conflict(new ApiError("validation_error", "Attribute already has values and cannot be deleted."));

        db.CategoryAttributes.Remove(attribute);
        await db.SaveChangesAsync();
        await categories.RebuildAsync();
        return Ok(); 
    }

    private static IReadOnlyCollection<string> NormalizeOptions(IReadOnlyCollection<string> options) =>
        options
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeSlug(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var pendingDash = false;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                pendingDash = false;
            }
            else if (ch is ' ' or '_' or '-')
            {
                if (!pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                    pendingDash = true;
                }
            }
        }

        return builder.ToString().Trim('-');
    }
}
