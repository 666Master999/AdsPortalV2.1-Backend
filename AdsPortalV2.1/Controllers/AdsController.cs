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
                // Placeholder edit to refresh context for relevance ordering change
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
    IAdDetailsService _adDetailsService,
    IAuthorizationService _authorizationService,
    PermissionService perms,
    IDomainEventPublisher _events,
    INotificationFactory _notificationFactory,
    INotificationService _notifications,
    IAdImagePatchService _imagePatchService) : ControllerBase
{
    // Use centralized helper
    private static string EnsurePublicPath(string? path) => AdsPortalV2.Models.FilePathHelpers.EnsurePublicPath(path);
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
    public async Task<ActionResult<AdDto>> PatchModerationStatus(int id, [FromBody] UpdateModerationRequest req, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var actorId))
            return Unauthorized();

        var ad = await db.Ads.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (ad == null)
            return NotFound();

        var previousStatus = ad.Status;
        ad.Status = req.Status;
        ad.UpdatedAt = DateTime.UtcNow;

        Notification? notification = null;
        if (req.Status != previousStatus && req.Status is AdStatus.Active or AdStatus.Rejected)
        {
            var actorName = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == actorId)
                .Select(u => u.UserName ?? u.UserLogin)
                .FirstAsync(cancellationToken);

            // Save rejection reason when applicable
            if (req.Status == AdStatus.Rejected && string.IsNullOrWhiteSpace(req.Reason))
            {
                return BadRequest(new ApiError("validation_error", "Reason is required when rejecting an ad."));
            }
            ad.RejectionReason = req.Reason;

            notification = req.Status == AdStatus.Active
                ? _notificationFactory.CreateAdApproved(ad.UserId, ad.Id, ad.Title, actorName)
                : _notificationFactory.CreateAdRejected(ad.UserId, ad.Id, ad.Title, ad.RejectionReason ?? "Не указана", actorName);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (req.Status == AdStatus.Active)
        {
            _events.Publish(new AdApproved(ad.Id, actorId));
        }
        else if (req.Status == AdStatus.Rejected)
        {
            _events.Publish(new AdRejected(ad.Id, actorId, ad.RejectionReason));
        }

        Log.AdModeration(_logger, id, previousStatus, req.Status);

        if (notification != null)
            await _notifications.SendAsync(notification, cancellationToken);

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
        [FromForm] int? mainImageIndex)
    {
        List<PatchIssueDto> issues = [];
        if (string.IsNullOrWhiteSpace(req.Title)) issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Title, "Title is required."));
        if (!req.CategoryId.HasValue || req.CategoryId.Value <= 0) issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.CategoryId, "CategoryId is required."));
        if (!req.LocationId.HasValue || req.LocationId.Value <= 0) issues.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.LocationId, "LocationId is required."));

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

        db.Ads.Add(ad);
        await db.SaveChangesAsync();
        _events.Publish(new AdCreated(ad.Id, creatorId));

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

        if (!User.TryGetUserId(out _))
            return Unauthorized();

        var canEdit = (await _authorizationService.AuthorizeAsync(User, ad, AuthorizationPolicies.CanEditAd)).Succeeded;
        if (!canEdit)
            return Forbid();

        HashSet<string> updated = [];
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
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            [AdFieldNames.Title] = v => PatchHelpers.UpdateString(v, ad.Title, x => ad.Title = x, AdFieldNames.Title, updated, skipped, errors),
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
