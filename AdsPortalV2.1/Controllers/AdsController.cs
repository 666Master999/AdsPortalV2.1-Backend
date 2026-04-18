using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
namespace AdsPortalV2.Controllers;

[ApiController]
[Route("ads")]
public class AdsController(
    AppDbContext db,
    ImageService _imageService,
    IMemoryCache _cache,
    ILogger<AdsController> _logger,
    AdQueryService _adQueryService,
    ICategoryService _categoryService,
    AdVisibilityService _adVisibility,
    IAdDetailsService _adDetailsService,
    IAuthorizationService _authorizationService,
    PermissionService perms,
    INotificationFactory _notificationFactory,
    INotificationService _notifications,
    IAdImagePatchService _imagePatchService) : ControllerBase
{
    // Use centralized helper
    private static string EnsurePublicPath(string? path) => AdsPortalV2.Models.FilePathHelpers.EnsurePublicPath(path);
    private static readonly Dictionary<string, Expression<Func<Ad, object?>>> _sortMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [AdFieldNames.Title] = ad => ad.Title,
        [AdFieldNames.CategoryId] = ad => ad.CategoryId,
        [AdFieldNames.Price] = ad => ad.Price,
        [AdFieldNames.CreatedAt] = ad => ad.CreatedAt,
        [AdFieldNames.UpdatedAt] = ad => ad.UpdatedAt,
        [AdFieldNames.Views] = ad => ad.ViewsCount,
        [AdFieldNames.Favorites] = ad => ad.FavoritesCount,
    };

    private static readonly HashSet<string> ReservedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "search",
        "location",
        "category",
        "includeChildren",
        "priceFrom",
        "priceTo",
        "dateFrom",
        "dateTo",
        "userId",
        "status",
        "type",
        "page",
        "pageSize",
        "sort",
        "cursor"
    };

    private static IReadOnlyCollection<string> ParseQueryValues(StringValues values)
    {
        var parsed = new List<string>();
        foreach (var raw in values)
        {
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var value = part.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    parsed.Add(value);
            }
        }

        return parsed
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryParseSlugFilters(
        IQueryCollection query,
        IReadOnlyDictionary<string, IReadOnlyCollection<CategoryAttributeDto>> attributeLookup,
        out List<AdAttributeFilterDto> filters,
        out string? error)
    {
        filters = [];
        error = null;

        var unknownKeys = query.Keys
            .Where(key => !ReservedQueryKeys.Contains(key) && !attributeLookup.ContainsKey(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (unknownKeys.Length > 0)
        {
            error = $"Unknown filter key: '{unknownKeys[0]}'.";
            return false;
        }

        var result = new List<AdAttributeFilterDto>();
        foreach (var (slug, attributes) in attributeLookup)
        {
            if (!query.TryGetValue(slug, out var rawValues))
                continue;

            var values = ParseQueryValues(rawValues);
            if (values.Count == 0)
            {
                error = $"Filter '{slug}' cannot be empty.";
                return false;
            }

            var attributeIds = attributes.Select(a => a.Id).Distinct().ToArray();
            foreach (var attribute in attributes)
            {
                if (!ValidateFilterValues(attribute, values, out var validationError))
                {
                    error = validationError;
                    return false;
                }
            }

            result.Add(new AdAttributeFilterDto(attributeIds, values));
        }

        filters = result;
        return true;
    }

    private static bool ValidateFilterValues(CategoryAttributeDto attribute, IReadOnlyCollection<string> values, out string? error)
    {
        error = null;

        foreach (var value in values)
        {
            switch (attribute.Type)
            {
                case AttributeType.Enum:
                    if (!attribute.Options.Any(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase)))
                    {
                        error = $"Invalid value for filter '{attribute.Slug}'.";
                        return false;
                    }
                    break;
                case AttributeType.Int:
                    if (!int.TryParse(value, out _))
                    {
                        error = $"Filter '{attribute.Slug}' must be an integer.";
                        return false;
                    }
                    break;
                case AttributeType.Decimal:
                    if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _) &&
                        !decimal.TryParse(value, out _))
                    {
                        error = $"Filter '{attribute.Slug}' must be a decimal.";
                        return false;
                    }
                    break;
                case AttributeType.Bool:
                    if (!bool.TryParse(value, out _))
                    {
                        error = $"Filter '{attribute.Slug}' must be a boolean.";
                        return false;
                    }
                    break;
            }
        }

        return true;
    }

    // PATCH: /ads/{id}/moderation
    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpPatch("{id}/moderation")]
    public async Task<ActionResult<AdDto>> PatchModerationStatus(int id, [FromBody] UpdateModerationRequest req, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var actorId))
            return Unauthorized();

        var ad = await db.Ads.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (ad == null)
            return NotFound();

        var previousStatus = ad.Status;

        string? actorName = null;
        if (req.Status != previousStatus && req.Status is AdStatus.Active or AdStatus.Rejected)
        {
            actorName = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == actorId)
                .Select(u => u.UserName ?? u.UserLogin)
                .FirstAsync(cancellationToken);

            // Save rejection reason when applicable
            if (req.Status == AdStatus.Rejected && string.IsNullOrWhiteSpace(req.Reason))
            {
                return BadRequest(new ApiError("validation_error", "Reason is required when rejecting an ad."));
            }

            if (req.Status == AdStatus.Active)
            {
                ad.Approve(actorId, actorName ?? string.Empty);
            }
            else
            {
                ad.Reject(actorId, req.Reason ?? string.Empty, actorName ?? string.Empty);
            }
        }
        else
        {
            ad.Status = req.Status;
            ad.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        try { _logger.LogWarning("CONTROLLER SAVED: AdId={AdId}", ad.Id); } catch {}

        Log.AdModeration(_logger, id, previousStatus, req.Status);

        // Notifications and audit logs are handled by domain event handlers.

        return Ok(new AdDto(ad.Id, ad.UserId, ad.CategoryId, ad.Title, ad.Description, ad.Price, ad.ListingType, ad.IsNegotiable,
            ad.LocationId, ad.CreatedAt, ad.UpdatedAt, (AdStatus)ad.Status, ad.RejectionReason, ad.DeletedAt, ad.ViewsCount, ad.FavoritesCount));
    }
    // GET: /ads/moderation
    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpGet("moderation")]
    public async Task<ActionResult<IReadOnlyCollection<ModerationAdDto>>> GetModerationList()
    {
        var result = await db.Ads
            .Where(ad => ad.Status == AdStatus.PendingModeration)
            .OrderByDescending(ad => ad.CreatedAt)
            .Select(ad => new ModerationAdDto(
                ad.Id,
                ad.Title,
                ad.Description,
                ad.Price,
                ad.CategoryId,
                ad.LocationId,
                ad.Location == null ? null : new LocationRef(ad.Location.Type, ad.Location.Id, ad.Location.Name),
                ad.ListingType,
                ad.CreatedAt,
                ad.UpdatedAt,
                ad.UserId,
                ad.User != null ? ad.User.UserName : null,
                ad.User != null ? ad.User.UserLogin : null,
                EnsurePublicPath(db.AdImages.Where(img => img.Id == ad.MainImageId).Select(img => img.FilePath).FirstOrDefault())))
            .ToListAsync();

        return Ok(result);
    }
    [HttpGet]
    public async Task<ActionResult<PagedResultDto<AdListItemDto>>> GetAll([FromQuery] AdsQuery q, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 50);

        if (!TryParseIds(q.Location, out var locationIds))
            return BadRequest(new ApiError("validation_error", "Invalid location format. Expected comma-separated integers."));

        if (locationIds.Length > 10)
            return BadRequest(new ApiError("validation_error", "Too many locationIds. Maximum is 10."));

        if (!TryParseIds(q.Category, out var categoryIds))
            return BadRequest(new ApiError("validation_error", "Invalid category format. Expected comma-separated integers."));

        if (q.IncludeChildren && categoryIds.Length > 0)
        {
            // Expand categories using category graph children lookup (expansion kept local to caller).
            var graph = await _categoryService.GetGraphAsync(cancellationToken);
            var expanded = new HashSet<int>(categoryIds);
            var queue = new Queue<int>(categoryIds);

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!graph.ChildrenById.TryGetValue(id, out var children))
                    continue;

                foreach (var child in children)
                {
                    if (expanded.Add(child))
                        queue.Enqueue(child);
                }
            }

            categoryIds = expanded.ToArray();
        }

        var hasAttributeKeys = Request.Query.Keys.Any(key => !ReservedQueryKeys.Contains(key));
        if (hasAttributeKeys && categoryIds.Length == 0)
            return BadRequest(new ApiError("validation_error", "Category is required when attribute filters are used."));

        var attributeLookup = categoryIds.Length == 0
            ? new Dictionary<string, IReadOnlyCollection<CategoryAttributeDto>>(StringComparer.OrdinalIgnoreCase)
            : await _categoryService.GetAttributeLookupAsync(categoryIds, onlyFilters: true, cancellationToken);

        if (!TryParseSlugFilters(Request.Query, attributeLookup, out var attributeFilters, out var filterError))
            return BadRequest(new ApiError("validation_error", filterError ?? "Invalid attribute filters."));

        if (q.Search?.Length > 100)
            return BadRequest(new ApiError("validation_error", "Search query is too long. Maximum is 100 characters."));

        var sort = string.IsNullOrWhiteSpace(q.Sort)
            ? (string.IsNullOrWhiteSpace(q.Search) ? "-createdAt" : "-relevance")
            : q.Sort;
        var descending = sort.StartsWith('-');
        var sortKey = descending ? sort[1..] : sort;

        if (!sortKey.Equals(AdFieldNames.Relevance, StringComparison.OrdinalIgnoreCase) && !_sortMap.ContainsKey(sortKey))
            return BadRequest(new ApiError("validation_error", $"Unknown sort field: '{sortKey}'."));

        AdStatus? status = null;
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            if (!Enum.TryParse<AdStatus>(q.Status, true, out var parsedStatus))
                return BadRequest(new ApiError("validation_error", "Invalid status value."));

            status = parsedStatus;
        }

        int? currentUserId = User.TryGetUserId(out var uid) ? uid : null;
        var hasViewHidden = (await _authorizationService.AuthorizeAsync(User, null, AuthorizationPolicies.CanViewHiddenAd)).Succeeded;

        var query = await _adVisibility.ApplyVisibilityAsync(db.Ads.AsNoTracking(), currentUserId);
        var result = await _adQueryService.BuildQueryAsync(
            query,
            q,
            locationIds,
            categoryIds,
            attributeFilters,
            status,
            page,
            pageSize,
            sortKey,
            descending,
            currentUserId,
            hasViewHidden,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AdDetailsDto>> GetById(int id)
    {
        int? userId = User.TryGetUserId(out var uid) ? uid : null;
        var hasViewHidden = (await _authorizationService.AuthorizeAsync(User, null, AuthorizationPolicies.CanViewHiddenAd)).Succeeded;

        var adInfo = await db.Ads.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new AdAccessInfo(a.UserId, a.Status))
            .FirstOrDefaultAsync();

        if (adInfo == null)
            return NotFound(new ApiError("not_found", "Объявление не найдено."));

        var canView = await _adVisibility.CanViewAdAsync(userId, adInfo.UserId, adInfo.Status);
        bool isOwner = userId.HasValue && adInfo.UserId == userId.Value;
        if (!canView)
            return StatusCode(403, new ApiError("forbidden", "У вас нет прав доступа к этому объявлению."));

        if (!isOwner && userId.HasValue && !User.IsPrivileged())
        {
            var cacheKey = $"view:{id}:{userId}";
            if (!_cache.TryGetValue(cacheKey, out _))
            {
                _cache.Set(cacheKey, true, TimeSpan.FromHours(24));
                await db.Ads.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ViewsCount, a => a.ViewsCount + 1));
            }
        }

        var dto = await _adDetailsService.GetAsync(id, userId, hasViewHidden || isOwner);
        if (dto == null)
            return NotFound(new ApiError("not_found", "Объявление не найдено."));

        return Ok(dto);
    }

    // POST: /ads (multipart/form-data)
    [Authorize]
    [EnableRateLimiting("CreateAdPerDay")]
    [HttpPost]
    public async Task<ActionResult<CreateAdResultDto>> Create(
        [FromForm] Models.CreateAdRequest req,
        [FromForm] List<IFormFile>? files,
        [FromForm] int? mainImageIndex,
        CancellationToken cancellationToken = default)
    {
        List<PatchIssueDto> issues = [];
        if (string.IsNullOrWhiteSpace(req.Title)) issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Title, "Title is required."));
        else if (req.Title.Trim().Length > 200) issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Title, "Title is too long. Maximum is 200 characters."));
        if (req.Price.HasValue && req.Price < 0) issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Price, "Price must be non-negative."));
        if (!req.CategoryId.HasValue || req.CategoryId.Value <= 0) issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.CategoryId, "CategoryId is required."));
        if (!req.LocationId.HasValue || req.LocationId.Value <= 0) issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.LocationId, "LocationId is required."));

        var category = req.CategoryId.HasValue && req.CategoryId.Value > 0
            ? await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CategoryId.Value)
            : null;

        if (req.CategoryId.HasValue && req.CategoryId.Value > 0 && category == null)
            issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.CategoryId, "Category was not found."));

        var attributeValues = new List<AdAttributeValue>();
        if (category != null)
        {
            var categoryAttributes = await _categoryService.GetAttributesAsync(category.Id);

            var providedValues = (req.AttributeValues ?? [])
                .GroupBy(v => v.AttributeId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var attribute in categoryAttributes)
            {
                if (!providedValues.TryGetValue(attribute.Id, out var provided))
                {
                    if (attribute.IsRequired)
                        issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, $"attributeValues.{attribute.Id}", $"Attribute '{attribute.Name}' is required."));

                    continue;
                }

                var rawValue = provided.Value?.Trim();
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, $"attributeValues.{attribute.Id}", $"Attribute '{attribute.Name}' cannot be empty."));
                    continue;
                }

                if (attribute.Type == AttributeType.Enum)
                {
                    var allowed = attribute.Options.Select(o => o.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!allowed.Contains(rawValue))
                    {
                        issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, $"attributeValues.{attribute.Id}", $"Invalid value for attribute '{attribute.Name}'."));
                        continue;
                    }
                }
                else if (attribute.Type == AttributeType.Int)
                {
                    if (!int.TryParse(rawValue, out _))
                    {
                        issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, $"attributeValues.{attribute.Id}", $"Attribute '{attribute.Name}' must be an integer."));
                        continue;
                    }
                }
                else if (attribute.Type == AttributeType.Bool)
                {
                    if (!bool.TryParse(rawValue, out _))
                    {
                        issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, $"attributeValues.{attribute.Id}", $"Attribute '{attribute.Name}' must be a boolean."));
                        continue;
                    }
                }
                else if (attribute.Type == AttributeType.Decimal)
                {
                    if (!decimal.TryParse(rawValue, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _) &&
                        !decimal.TryParse(rawValue, out _))
                    {
                        issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, $"attributeValues.{attribute.Id}", $"Attribute '{attribute.Name}' must be a decimal."));
                        continue;
                    }
                }

                attributeValues.Add(new AdAttributeValue
                {
                    AttributeId = attribute.Id,
                    Value = rawValue
                });
            }

            foreach (var provided in req.AttributeValues ?? [])
            {
                if (!categoryAttributes.Any(a => a.Id == provided.AttributeId))
                    issues.Add(new PatchIssueDto(PatchErrorCodes.NotAllowed, $"attributeValues.{provided.AttributeId}", "Attribute is not allowed for the selected category."));
            }
        }

        if (issues.Count > 0)
            return BadRequest(new ApiError("validation_error", "Validation failed.", issues));

        if (!User.TryGetUserId(out var creatorId))
            return Unauthorized();

        var now = DateTime.UtcNow;
        if (await perms.HasActiveRestrictionAsync(creatorId, RestrictionType.PostBan))
            return StatusCode(403, new ApiError("post_banned", "You cannot create ads"));

        var ad = new Ad
        {
            Title = req.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            Price = req.Price,
            IsNegotiable = req.IsNegotiable ?? false,
            CategoryId = req.CategoryId,
            ListingType = string.IsNullOrWhiteSpace(req.ListingType) ? null : req.ListingType.Trim(),
            LocationId = req.LocationId!.Value,
            UserId = creatorId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = AdStatus.PendingModeration
        };

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        db.Ads.Add(ad);

        if (attributeValues.Count > 0)
        {
            foreach (var value in attributeValues)
                value.Ad = ad;

            db.AdAttributeValues.AddRange(attributeValues);
        }

        // Persist ad first so EF assigns identity
        await db.SaveChangesAsync(cancellationToken);

        // Now raise domain event on aggregate (Ad.Id is available) and persist outbox entries
        ad.MarkCreated(creatorId);
        db.MaterializeDomainEvents();
        await db.SaveChangesAsync(cancellationToken);

        // Clear in-memory events after they were persisted to Outbox
        db.ClearDomainEvents();

        await tx.CommitAsync(cancellationToken);

        Log.AdCreated(_logger, ad.Id, creatorId);

        if (files?.Count > 0)
        {
            var savedImages = await SaveAdImages(ad.Id, files, mainImageIndex);
            var mainImageId = await db.Ads.Where(a => a.Id == ad.Id).Select(a => a.MainImageId).FirstOrDefaultAsync();
            return Ok(new CreateAdResultDto(
                "Ad created with images successfully.",
                ad.Id,
                [.. savedImages.Select(img => new AdImageDto(img.Id, img.AdId, EnsurePublicPath(img.FilePath), img.SortOrder, img.Id == mainImageId))]));
        }

        return Ok(new CreateAdResultDto("Ad created successfully.", ad.Id));
    }

    // DELETE: /ads/5
    [Authorize]
    [HttpDelete("{id}")]
    public async Task<ActionResult<CreateAdResultDto>> Delete(int id)
    {
        var ad = await db.Ads.FirstOrDefaultAsync(ad => ad.Id == id && ad.Status != AdStatus.Deleted);
        if (ad == null)
            return NotFound();

        if (!User.TryGetUserId(out var currentUserId))
            return Unauthorized();

        var canDelete = (await _authorizationService.AuthorizeAsync(User, ad, AuthorizationPolicies.CanDeleteAd)).Succeeded;
        if (!canDelete)
            return Forbid();

        ad.Status = AdStatus.Deleted;
        ad.DeletedAt = DateTime.UtcNow;
        ad.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        Log.AdDeleted(_logger, id, currentUserId);
        return Ok(new CreateAdResultDto("Ad deleted successfully.", ad.Id));
    }

    [Authorize]
    [HttpPatch("{id}")]
    public async Task<ActionResult<PatchResultDto>> Update(int id, [FromBody] Dictionary<string, JsonElement> data)
    {
        var ad = await db.Ads.Include(a => a.Images).FirstOrDefaultAsync(a => a.Id == id);
        if (ad == null)
            return NotFound();

        if (!User.TryGetUserId(out var currentUserId))
            return Unauthorized();

        var canEdit = (await _authorizationService.AuthorizeAsync(User, ad, AuthorizationPolicies.CanEditAd)).Succeeded;
        if (!canEdit)
            return Forbid();

        var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<PatchIssueDto> skipped = [];
        List<PatchIssueDto> errors = [];

        var patchMap = BuildPatchMap(ad, updated, skipped, errors);

        foreach (var kv in data)
        {
            if (!patchMap.TryGetValue(kv.Key, out var handler))
                errors.Add(new PatchIssueDto(PatchErrorCodes.NotAllowed, kv.Key, $"Field '{kv.Key}' is not allowed."));
            else
                handler(kv.Value);
        }

        ad.UpdatedAt = DateTime.UtcNow;

        // If owner changed visible/critical fields, send ad back to moderation
        var requiresModeration =
            updated.Contains(AdFieldNames.Title) ||
            updated.Contains(AdFieldNames.Description) ||
            updated.Contains(AdFieldNames.Images);

        if (requiresModeration)
        {
            ad.Status = AdStatus.PendingModeration;
            ad.RejectionReason = null;
            db.AuditLogs.Add(new AuditLog { ActorUserId = currentUserId, TargetUserId = ad.UserId, Action = "ad.send_to_moderation", TargetType = "Ad", TargetId = id });
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            try { _logger.LogError(ex, "Ad update save failed: AdId={AdId}", id); } catch {}

            var env = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var msg = env.IsDevelopment() ? (ex.InnerException?.Message ?? ex.Message) : "Internal error while saving changes.";
            errors.Add(new PatchIssueDto(PatchErrorCodes.InternalError, null, msg));

            // Return structured patch result with error details
            return BadRequest(new PatchResultDto(false, updated, skipped, errors));
        }
        catch (Exception ex)
        {
            try { _logger.LogError(ex, "Unexpected error while saving ad update: AdId={AdId}", id); } catch {}

            var env = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var msg = env.IsDevelopment() ? ex.Message : "Internal error while saving changes.";
            errors.Add(new PatchIssueDto(PatchErrorCodes.InternalError, null, msg));
            return BadRequest(new PatchResultDto(false, updated, skipped, errors));
        }

        var success = errors.Count == 0;
        return success
            ? Ok(new PatchResultDto(true, updated, skipped, errors))
            : updated.Count > 0
                ? Ok(new PatchResultDto(false, updated, skipped, errors))
                : BadRequest(new PatchResultDto(false, updated, skipped, errors));
    }

    private Dictionary<string, Action<JsonElement>> BuildPatchMap(
        Ad ad,
        ICollection<string> updated,
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            [AdFieldNames.Title] = v =>
            {
                if (v.ValueKind is JsonValueKind.Undefined) return;
                if (v.ValueKind is JsonValueKind.Null)
                {
                    errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Title, "Title cannot be null."));
                    return;
                }
                if (v.ValueKind is not JsonValueKind.String)
                {
                    errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Title, "Title must be a string."));
                    return;
                }
                var val = v.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(val))
                {
                    errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Title, "Title cannot be empty."));
                    return;
                }
                if (val.Length > 200)
                {
                    errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Title, "Title is too long. Maximum is 200 characters."));
                    return;
                }
                if (string.Equals(val, ad.Title, StringComparison.Ordinal))
                {
                    skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, AdFieldNames.Title, "Title not changed."));
                    return;
                }
                ad.Title = val;
                updated.Add(AdFieldNames.Title);
            },
            [AdFieldNames.Description] = v => PatchHelpers.UpdateNullableString(v, ad.Description, x => ad.Description = x, AdFieldNames.Description, updated, skipped, errors),
            [AdFieldNames.Price] = v =>
            {
                if (v.ValueKind is JsonValueKind.Undefined) return;
                if (v.ValueKind is JsonValueKind.Null)
                {
                    // allow null
                    if (ad.Price is null)
                    {
                        skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, AdFieldNames.Price, "Price not changed."));
                        return;
                    }
                    ad.Price = null;
                    updated.Add(AdFieldNames.Price);
                    return;
                }
                if (!v.TryGetDecimal(out var dec))
                {
                    errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Price, "Price must be a decimal."));
                    return;
                }
                if (dec < 0)
                {
                    errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Price, "Price must be non-negative."));
                    return;
                }
                if (EqualityComparer<decimal?>.Default.Equals(dec, ad.Price))
                {
                    skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, AdFieldNames.Price, "Price not changed."));
                    return;
                }
                ad.Price = dec;
                updated.Add(AdFieldNames.Price);
            },
            [AdFieldNames.IsNegotiable] = v => PatchHelpers.UpdateBool(v, ad.IsNegotiable, x => ad.IsNegotiable = x, AdFieldNames.IsNegotiable, updated, skipped, errors),
            [AdFieldNames.CategoryId] = v => PatchHelpers.UpdateNullableInt(v, ad.CategoryId, x => ad.CategoryId = x, AdFieldNames.CategoryId, updated, skipped, errors),
            [AdFieldNames.ListingType] = v => PatchHelpers.UpdateString(v, ad.ListingType, x => ad.ListingType = x, AdFieldNames.ListingType, updated, skipped, errors),
            [AdFieldNames.LocationId] = v => PatchHelpers.UpdateInt(v, ad.LocationId, x => ad.LocationId = x, AdFieldNames.LocationId, updated, skipped, errors),
            [AdFieldNames.MainImageId] = v => PatchHelpers.UpdateNullableInt(v, ad.MainImageId, x => ad.MainImageId = x, AdFieldNames.MainImageId, updated, skipped, errors),
            [AdFieldNames.Images] = v => _imagePatchService.Apply(ad, v, updated, skipped, errors)
        };
    }

    private async Task<List<AdImage>> SaveAdImages(int adId, List<IFormFile> files, int? mainImageIndex)
    {
        if (!User.TryGetUserId(out var ownerId)) throw new UnauthorizedAccessException();
        var userIdStr = ownerId.ToString();
        var uploadPath = Path.Combine("wwwroot", "files", userIdStr, "Ads", adId.ToString());
        Directory.CreateDirectory(uploadPath);

        List<AdImage> savedImages = [];

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var fileName = Path.GetRandomFileName() + Path.GetExtension(file.FileName);
            await using var stream = file.OpenReadStream();
            await _imageService.SaveCompressedImageAsync(stream, uploadPath, fileName);

            var relativePath = Path.Combine("files", userIdStr, "Ads", adId.ToString(), fileName);

            var adImage = new AdImage
            {
                AdId = adId,
                FilePath = relativePath,
                SortOrder = i
            };

            db.AdImages.Add(adImage);
            savedImages.Add(adImage);
        }

        await db.SaveChangesAsync();

        if (savedImages.Count > 0)
        {
            var index = mainImageIndex.HasValue && mainImageIndex.Value >= 0 && mainImageIndex.Value < savedImages.Count
                ? mainImageIndex.Value
                : 0;
            var mainImageId = savedImages[index].Id;
            await db.Ads.Where(a => a.Id == adId).ExecuteUpdateAsync(s => s.SetProperty(a => a.MainImageId, mainImageId));
        }

        return savedImages;
    }

    [Authorize]
    [HttpPost("{id}/upload")]
    public async Task<ActionResult<UploadFilesResultDto>> UploadAdImages(int id, [FromForm(Name = "files")] IFormFileCollection files)
    {
        var ad = await db.Ads.FindAsync(id);
        if (ad == null)
            return NotFound();

        if (!User.TryGetUserId(out var uploadUserId))
            return Unauthorized();

        var canModerate = (await _authorizationService.AuthorizeAsync(User, null, AuthorizationPolicies.CanModerateAd)).Succeeded;
        if (ad.UserId != uploadUserId && !canModerate)
            return Forbid();

        if (files.Count == 0)
            return BadRequest(new ApiError("validation_error", "No files provided."));

        var uploadDir = Path.Combine("wwwroot", "files", uploadUserId.ToString(), "Ads", id.ToString());
        Directory.CreateDirectory(uploadDir);

        var uploadedPaths = new List<string>();

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName);
            var fileName = _imageService.GenerateShortFileName(ext);
            await using var stream = file.OpenReadStream();
            await _imageService.SaveCompressedImageAsync(stream, uploadDir, fileName);

            var relativePath = Path.Combine("files", uploadUserId.ToString(), "Ads", id.ToString(), fileName).Replace('\\', '/');
            uploadedPaths.Add(FilePathHelpers.EnsurePublicPath(relativePath));
        }

        return Ok(new UploadFilesResultDto(uploadedPaths));
    }

    private static bool TryParseIds(string? value, out int[] ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var items = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var parsed = new int[items.Length];

        for (var i = 0; i < items.Length; i++)
            if (!int.TryParse(items[i], out parsed[i]))
                return false;

        ids = [.. parsed.Distinct()];
        return true;
    }

}

sealed record AdAccessInfo(int UserId, AdStatus Status);
