using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Linq.Expressions;
using System.Text.Json;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("ads")]
public class AdsController(
    AppDbContext db,
    ImageService _imageService,
    IMemoryCache _cache,
    ILogger<AdsController> _logger,
    AdQueryService _adQueryService,
    AdVisibilityService _adVisibility,
    IAuthorizationService _authorizationService,
    PermissionService perms,
    IDomainEventPublisher _events,
    INotificationFactory _notificationFactory,
    INotificationService _notifications,
    IAdImagePatchService _imagePatchService) : ControllerBase
{
    private static readonly Dictionary<string, Expression<Func<Ad, object?>>> _sortMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [AdFieldNames.Title] = ad => ad.Title,
        [AdFieldNames.Price] = ad => ad.Price,
        [AdFieldNames.CreatedAt] = ad => ad.CreatedAt,
        [AdFieldNames.UpdatedAt] = ad => ad.UpdatedAt,
        [AdFieldNames.Views] = ad => ad.ViewsCount,
        [AdFieldNames.Favorites] = ad => ad.FavoritesCount,
    };

    // PATCH: /ads/{id}/moderation
    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpPatch("{id}/moderation")]
    public async Task<IActionResult> PatchModerationStatus(int id, [FromBody] AdStatus status)
    {
        if (!User.TryGetUserId(out var actorId))
            return Unauthorized();

        var ad = await db.Ads.FirstOrDefaultAsync(a => a.Id == id);
        if (ad == null)
            return NotFound();

        var previousStatus = ad.Status;
        ad.Status = status;
        ad.UpdatedAt = DateTime.UtcNow;

        Notification? notification = null;
        if (status != previousStatus && status is AdStatus.Active or AdStatus.Rejected)
        {
            var actorName = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == actorId)
                .Select(u => u.UserName ?? u.UserLogin)
                .FirstAsync();

            notification = status == AdStatus.Active
                ? _notificationFactory.CreateAdApproved(ad.UserId, ad.Id, ad.Title, actorName)
                : _notificationFactory.CreateAdRejected(ad.UserId, ad.Id, ad.Title, ad.RejectionReason ?? "Не указана", actorName);
        }

        await db.SaveChangesAsync();
        _events.Publish(status == AdStatus.Active
            ? new AdApproved(ad.Id, actorId)
            : status == AdStatus.Rejected
                ? new AdRejected(ad.Id, actorId, ad.RejectionReason)
                : new AdCreated(ad.Id, ad.UserId));

        Log.AdModeration(_logger, id, previousStatus, status);

        if (notification != null)
            await _notifications.SendAsync(notification);

        return Ok(new AdDto(ad.Id, ad.UserId, ad.CategoryId, ad.Title, ad.Description, ad.Price, ad.ListingType, ad.IsNegotiable,
            ad.LocationId, ad.CreatedAt, ad.UpdatedAt, (AdStatus)ad.Status, ad.RejectionReason, ad.DeletedAt, ad.ViewsCount, ad.FavoritesCount));
    }
    // GET: /ads/moderation
    [Authorize(Policy = AuthorizationPolicies.CanModerateAd)]
    [HttpGet("moderation")]
    public async Task<IActionResult> GetModerationList()
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
                db.AdImages.Where(img => img.Id == ad.MainImageId).Select(img => img.FilePath).FirstOrDefault()))
            .ToListAsync();

        return Ok(result);
    }
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] AdsQuery q)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 50);

        if (!TryParseIds(q.Location, out var locationIds))
            return BadRequest(new ApiError("validation_error", "Invalid location format. Expected comma-separated integers."));

        if (locationIds.Length > 10)
            return BadRequest(new ApiError("validation_error", "Too many locationIds. Maximum is 10."));

        if (!TryParseIds(q.Category, out var categoryIds))
            return BadRequest(new ApiError("validation_error", "Invalid category format. Expected comma-separated integers."));

        var sort = string.IsNullOrWhiteSpace(q.Sort) ? "-createdAt" : q.Sort;
        var descending = sort.StartsWith('-');
        var sortKey = descending ? sort[1..] : sort;

        if (!_sortMap.TryGetValue(sortKey, out var sortExpr))
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

        query = await _adQueryService.BuildQueryAsync(query, q, locationIds, categoryIds, status);
        query = descending ? query.OrderByDescending(sortExpr) : query.OrderBy(sortExpr);

        if (!sortKey.Equals("createdAt", StringComparison.OrdinalIgnoreCase))
            query = ((IOrderedQueryable<Ad>)query).ThenByDescending(ad => ad.CreatedAt);

        var total = await query.CountAsync();
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling((double)total / pageSize);
        page = Math.Min(page, totalPages);

        var items = await query.Select(ad => new AdListItemDto
        {
            Id = ad.Id,
            Title = ad.Title,
            Description = ad.Description,
            Price = ad.Price,
            IsNegotiable = ad.IsNegotiable,
            CategoryId = ad.CategoryId,
            LocationId = ad.LocationId,
            Location = ad.Location == null ? null : new LocationRef(
                ad.Location.Type,
                ad.Location.Id,
                ad.Location.Name),
            ListingType = ad.ListingType,
            CreatedAt = ad.CreatedAt,
            UpdatedAt = ad.UpdatedAt,
            UserId = ad.UserId,
            ViewsCount = ad.ViewsCount,
            FavoritesCount = ad.FavoritesCount,
            MainImageUrl = db.AdImages
                .Where(img => img.Id == ad.MainImageId)
                .Select(img => img.FilePath)
                .FirstOrDefault(),
            IsFavorite = currentUserId.HasValue && db.UserFavoriteAds.Any(f => f.UserId == currentUserId.Value && f.AdId == ad.Id),
            ModerationStatus = hasViewHidden || (currentUserId.HasValue && ad.UserId == currentUserId.Value) ? (AdStatus?)ad.Status : null
        }).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new PagedResultDto<AdListItemDto>(items, total, page, pageSize, totalPages));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
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

        var dto = await db.Ads.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new AdDetailsDto(
                a.Id,
                a.UserId,
                a.CategoryId,
                a.Title,
                a.Description,
                a.Price,
                a.IsNegotiable,
                a.LocationId,
                a.Location == null ? null : new LocationRef(a.Location.Type, a.Location.Id, a.Location.Name),
                a.ListingType,
                a.CreatedAt,
                a.UpdatedAt,
                (hasViewHidden || isOwner) ? a.Status : null,
                (hasViewHidden || isOwner) ? a.RejectionReason : null,
                a.DeletedAt,
                a.Category == null ? null : new AdCategoryDto(a.Category.Id, a.Category.Name, a.Category.ParentId),
                a.User == null ? null : new AdOwnerDto(
                    a.User.Id,
                    a.User.UserLogin,
                    a.User.UserName,
                    a.User.UserEmail,
                    a.User.UserPhoneNumber,
                    a.User.AvatarPath,
                    Array.Empty<string>(),
                    a.User.CreatedAt,
                    a.User.LastActivityAt),
                Array.Empty<AdImageDto>(),
                userId.HasValue && db.UserFavoriteAds.Any(fav => fav.UserId == userId.Value && fav.AdId == a.Id)))
            .FirstAsync();

        if (dto.User != null)
        {
            var roleNames = await db.UserRoles
                .AsNoTracking()
                .Where(ur => ur.UserId == dto.UserId)
                .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, role => role.Id, (_, role) => role.Name)
                .ToListAsync();

            dto = dto with { User = dto.User with { Roles = roleNames } };
        }

        if (dto.Images.Count == 0)
        {
            dto = dto with
            {
                Images = await db.AdImages
                    .AsNoTracking()
                    .Where(img => img.AdId == id)
                    .OrderBy(img => img.SortOrder)
                    .Select(img => new AdImageDto(img.Id, img.AdId, img.FilePath, img.SortOrder))
                    .ToListAsync()
            };
        }

        return Ok(dto);
    }

    // POST: /ads (multipart/form-data)
    [Authorize]
    [EnableRateLimiting("CreateAdPerDay")]
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromForm] Models.CreateAdRequest req,
        [FromForm] List<IFormFile>? files,
        [FromForm] int? mainImageIndex)
    {
        Dictionary<string, string> fields = [];
        if (string.IsNullOrWhiteSpace(req.Title)) fields[AdFieldNames.Title] = "Title is required.";
        if (!req.CategoryId.HasValue || req.CategoryId.Value <= 0) fields[AdFieldNames.CategoryId] = "CategoryId is required.";
        if (!req.LocationId.HasValue || req.LocationId.Value <= 0) fields[AdFieldNames.LocationId] = "LocationId is required.";

        if (fields.Count > 0)
            return BadRequest(new ApiError("validation_error", "Validation failed.", fields));

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

        db.Ads.Add(ad);
        await db.SaveChangesAsync();
        _events.Publish(new AdCreated(ad.Id, creatorId));

        Log.AdCreated(_logger, ad.Id, creatorId);

        if (files?.Count > 0)
        {
            var savedImages = await SaveAdImages(ad.Id, files, mainImageIndex);
            return Ok(new CreateAdResultDto(
                "Ad created with images successfully.",
                ad.Id,
                [.. savedImages.Select(img => new AdImageDto(img.Id, img.AdId, img.FilePath, img.SortOrder))]));
        }

        return Ok(new CreateAdResultDto("Ad created successfully.", ad.Id));
    }

    // DELETE: /ads/5
    [Authorize]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
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
    public async Task<IActionResult> Update(int id, [FromBody] Dictionary<string, JsonElement> data)
    {
        var ad = await db.Ads.Include(a => a.Images).FirstOrDefaultAsync(a => a.Id == id);
        if (ad == null)
            return NotFound();

        if (!User.TryGetUserId(out _))
            return Unauthorized();

        var canEdit = (await _authorizationService.AuthorizeAsync(User, ad, AuthorizationPolicies.CanEditAd)).Succeeded;
        if (!canEdit)
            return Forbid();

        HashSet<string> updated = [];
        List<string> skipped = [];
        List<PatchErrorDto> errors = [];

        var patchMap = BuildPatchMap(ad, updated, skipped, errors);

        foreach (var kv in data)
        {
            if (!patchMap.TryGetValue(kv.Key, out var handler))
                errors.Add(new PatchErrorDto(PatchErrorCodes.NotAllowed, kv.Key, $"Field '{kv.Key}' is not allowed."));
            else
                handler(kv.Value);
        }

        ad.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

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
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors)
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            [AdFieldNames.Title] = v => PatchHelpers.UpdateString(v, ad.Title, x => ad.Title = x, AdFieldNames.Title, updated, skipped, errors, required: true),
            [AdFieldNames.Description] = v => PatchHelpers.UpdateNullableString(v, ad.Description, x => ad.Description = x, AdFieldNames.Description, updated, skipped, errors),
            [AdFieldNames.Price] = v => PatchHelpers.UpdateNullableDecimal(v, ad.Price, x => ad.Price = x, AdFieldNames.Price, updated, skipped, errors),
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
    public async Task<IActionResult> UploadAdImages(int id, List<IFormFile> files)
    {
        var ad = await db.Ads.FindAsync(id);
        if (ad == null)
            return NotFound();

        if (!User.TryGetUserId(out var uploadUserId))
            return Unauthorized();

        var canModerate = (await _authorizationService.AuthorizeAsync(User, null, AuthorizationPolicies.CanModerateAd)).Succeeded;
        if (ad.UserId != uploadUserId && !canModerate)
            return Forbid();

        if (files == null || files.Count == 0)
            return BadRequest(new ApiError("validation_error", "No files provided."));

        var uploadDir = Path.Combine("wwwroot", "files", uploadUserId.ToString(), "Ads", id.ToString());
        Directory.CreateDirectory(uploadDir);

        var uploadedPaths = new List<string>();

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var ext = Path.GetExtension(file.FileName);
            var fileName = _imageService.GenerateShortFileName(ext);
            await using var stream = file.OpenReadStream();
            await _imageService.SaveCompressedImageAsync(stream, uploadDir, fileName);

            var relativePath = Path.Combine("files", uploadUserId.ToString(), "Ads", id.ToString(), fileName).Replace('\\', '/');
            uploadedPaths.Add(relativePath);
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
