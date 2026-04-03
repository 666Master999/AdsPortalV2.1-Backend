using AdsPortalV2.Data;
using AdsPortalV2.Filters;
using AdsPortalV2.Entities;
using AdsPortalV2.Hubs;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("ads")]
public class AdsController(AppDbContext db, ImageService _imageService, IMemoryCache _cache, IHubContext<NotificationHub> _hub, ILogger<AdsController> _logger) : ControllerBase
{
    private static readonly HashSet<string> _allowedAdFields = ["Title", "Description", "Price", "IsNegotiable", "CategoryId", "Type", "CityId", "DistrictId"];

    private static readonly Dictionary<string, LambdaExpression> _sortMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"]     = (Expression<Func<Ad, string>>)(ad => ad.Title),
        ["price"]     = (Expression<Func<Ad, decimal?>>)(ad => ad.Price),
        ["created"]   = (Expression<Func<Ad, DateTime>>)(ad => ad.CreatedAt),
        ["createdAt"] = (Expression<Func<Ad, DateTime>>)(ad => ad.CreatedAt),
        ["updated"]   = (Expression<Func<Ad, DateTime>>)(ad => ad.UpdatedAt),
        ["updatedAt"] = (Expression<Func<Ad, DateTime>>)(ad => ad.UpdatedAt),
        ["views"]     = (Expression<Func<Ad, int>>)(ad => ad.ViewsCount),
        ["favorites"] = (Expression<Func<Ad, int>>)(ad => ad.FavoritesCount),
    };

    private static IQueryable<Ad> ApplySort(IQueryable<Ad> query, LambdaExpression keySelector, bool descending)
    {
        var method = descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy);
        var call = Expression.Call(typeof(Queryable), method,
            [typeof(Ad), keySelector.Body.Type],
            query.Expression, Expression.Quote(keySelector));
        return (IQueryable<Ad>)query.Provider.CreateQuery(call);
    }

    // PATCH: /ads/{id}/moderation
    [Authorize]
    [HttpPatch("{id}/moderation")]
    public async Task<IActionResult> PatchModerationStatus(int id, [FromBody] ModerationStatus status)
    {
        if (!User.TryGetUserId(out _) || !User.IsAdmin())
            return Forbid();

        var ad = await db.Ads.FirstOrDefaultAsync(a => a.Id == id);
        if (ad == null)
            return NotFound();

        var previousStatus = ad.ModerationStatus;
        ad.ModerationStatus = status;
        ad.UpdatedAt = DateTime.UtcNow;

        if (status != previousStatus && status is ModerationStatus.Approved or ModerationStatus.Rejected)
        {
            db.Notifications.Add(new Notification
            {
                UserId = ad.UserId,
                Type = status == ModerationStatus.Approved ? NotificationType.AdApproved : NotificationType.AdRejected,
                AdId = ad.Id
            });
        }

        await db.SaveChangesAsync();

        _logger.LogInformation("Ad moderation: id={AdId} {From} -> {To}", id, previousStatus, status);

        if (status != previousStatus && status is ModerationStatus.Approved or ModerationStatus.Rejected)
        {
            var type = status == ModerationStatus.Approved ? NotificationType.AdApproved : NotificationType.AdRejected;
            await _hub.Clients.Group($"user:{ad.UserId}").SendAsync("notification", new { AdId = ad.Id, Type = type });
        }

        return Ok(new { ad.Id, ad.ModerationStatus });
    }
    // GET: /ads/moderation
    [Authorize]
    [HttpGet("moderation")]
    public async Task<IActionResult> GetModerationList()
    {
        if (!User.TryGetUserId(out _) || !User.IsAdmin())
            return Forbid();

        var result = await db.Ads
            .Where(ad => !ad.IsDeleted && ad.ModerationStatus == ModerationStatus.Pending)
            .OrderByDescending(ad => ad.CreatedAt)
            .Select(ad => new
            {
                ad.Id,
                ad.Title,
                ad.Description,
                ad.Price,
                ad.CategoryId,
                ad.CityId,
                ad.DistrictId,
                City = ad.CityRef == null ? null : new LocationRef("city", ad.CityRef.Id, ad.CityRef.Name),
                District = ad.District == null ? null : new LocationRef("district", ad.District.Id, ad.District.Name),
                ad.Type,
                ad.CreatedAt,
                ad.UpdatedAt,
                ad.UserId,
                UserName = ad.User != null ? ad.User.UserName : null,
                UserLogin = ad.User != null ? ad.User.UserLogin : null,
                MainImageUrl = ad.Images
                    .Where(img => img.IsMain)
                    .Select(img => img.FilePath)
                    .FirstOrDefault()
            })
            .ToListAsync();

        return Ok(result);
    }
    // GET: /ads?sortBy=price&sortDir=asc&filters=[{"type":"search","value":"телефон"},{"type":"priceFrom","value":100}]
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] string? filters = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        bool descending = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);

        int? currentUserId = User.TryGetUserId(out var uid) ? uid : null;
        bool isAdmin = currentUserId.HasValue && User.IsAdmin();

        IQueryable<Ad> query = isAdmin
            ? db.Ads
            : db.Ads.Where(ad => !ad.IsDeleted && (ad.ModerationStatus == ModerationStatus.Approved || (currentUserId.HasValue && ad.UserId == currentUserId.Value)));

        if (!string.IsNullOrWhiteSpace(filters))
        {
            List<FilterRef> filterList;
            try { filterList = JsonSerializer.Deserialize<List<FilterRef>>(filters, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []; }
            catch { return BadRequest(new ApiError("validation_error", "Invalid filters format. Expected JSON array of {type, value}.")); }

            var handlers = BuildHandlers();
            foreach (var group in filterList.GroupBy(f => f.Type.ToLowerInvariant()))
            {
                if (!handlers.TryGetValue(group.Key, out var handler))
                    return BadRequest(new ApiError("validation_error", $"Unknown filter type: '{group.Key}'."));
                query = await handler.ApplyAsync(query, group.Key, group.Select(f => f.Value));
            }
        }

        var sortExpr = _sortMap.GetValueOrDefault(sortBy ?? "") ?? _sortMap["createdAt"];
        query = ApplySort(query, sortExpr, descending);
        // вторичная сортировка для стабильного порядка
        query = ((IOrderedQueryable<Ad>)query).ThenByDescending(ad => ad.CreatedAt);

        var totalCount = await query.CountAsync();
        var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);
        page = Math.Min(page, totalPages);

        var items = await query.Select(ad => new
        {
            ad.Id,
            ad.Title,
            ad.Description,
            ad.Price,
            ad.IsNegotiable,
            ad.CategoryId,
            City = ad.CityRef == null ? null : new LocationRef("city", ad.CityRef.Id, ad.CityRef.Name),
            District = ad.District == null ? null : new LocationRef("district", ad.District.Id, ad.District.Name),
            ad.Type,
            ad.CreatedAt,
            ad.UpdatedAt,
            ad.UserId,
            ad.ViewsCount,
            ad.FavoritesCount,
            MainImageUrl = ad.Images
                .Where(img => img.IsMain)
                .Select(img => img.FilePath)
                .FirstOrDefault(),
            IsFavorite = currentUserId.HasValue && db.UserFavoriteAds.Any(fav => fav.UserId == currentUserId.Value && fav.AdId == ad.Id),
            ModerationStatus = isAdmin || (currentUserId.HasValue && ad.UserId == currentUserId.Value) ? (ModerationStatus?)ad.ModerationStatus : null
        }).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new { items, totalCount, page, pageSize, totalPages });
    }

    // GET: /ads/5
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        // Получаем текущего пользователя
        int? userId = User.TryGetUserId(out var uid) ? uid : null;
        bool isAdmin = userId.HasValue && User.IsAdmin();

        var ad = await db.Ads.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new
            {
                a.Id,
                a.UserId,
                a.CategoryId,
                a.Title,
                a.Description,
                a.Price,
                a.IsNegotiable, // Добавляем флаг договорной цены в ответ
                a.CityId,
                a.DistrictId,
                City = a.CityRef == null ? null : new LocationRef("city", a.CityRef.Id, a.CityRef.Name),
                District = a.District == null ? null : new LocationRef("district", a.District.Id, a.District.Name),
                Region = a.CityRef == null ? null : new LocationRef("region", a.CityRef.RegionId, a.CityRef.Region!.Name),
                a.Type,
                a.CreatedAt,
                a.UpdatedAt,
                a.IsDeleted,
                ActualModerationStatus = a.ModerationStatus,
                Category = a.Category == null ? null : new { a.Category.Id, a.Category.Name, a.Category.ParentId },
                User = a.User == null ? null : new
                {
                    a.User.Id,
                    a.User.UserLogin,
                    a.User.UserName,
                    a.User.UserEmail,
                    a.User.UserPhoneNumber,
                    a.User.AvatarPath,
                    a.User.IsAdmin,
                    a.User.IsBlocked,
                    a.User.CreatedAt,
                    a.User.LastActivityAt
                },
                Images = a.Images.OrderBy(img => img.SortOrder).Select(img => new
                {
                    img.Id,
                    img.AdId,
                    img.FilePath,
                    img.SortOrder,
                    img.IsMain
                })
            })
            .FirstOrDefaultAsync();

        if (ad == null)
            return NotFound(new ApiError("not_found", "Объявление не найдено."));

        // Проверка доступа на основе реальных данных
        bool isOwner = userId.HasValue && ad.UserId == userId.Value;
        if (!isAdmin && !isOwner)
        {
            if (ad.IsDeleted || ad.ActualModerationStatus != ModerationStatus.Approved)
                return StatusCode(403, new ApiError("forbidden", "У вас нет прав доступа к этому объявлению."));
        }

        // Уникальный просмотр: считываем только если не сам владелец
        if (!isOwner && userId.HasValue)
        {
            var cacheKey = $"view:{id}:{userId}";
            if (!_cache.TryGetValue(cacheKey, out _))
            {
                _cache.Set(cacheKey, true, TimeSpan.FromHours(24));
                await db.Ads.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ViewsCount, a => a.ViewsCount + 1));
            }
        }

        // Вычисляем IsFavorite для текущего пользователя
        bool isFavorite = false;
        if (userId.HasValue)
        {
            isFavorite = await db.UserFavoriteAds.AnyAsync(fav => fav.UserId == userId.Value && fav.AdId == ad.Id);
        }

        // Формируем ответ, скрывая статус модерации от посторонних
        var response = new
        {
            ad.Id,
            ad.UserId,
            ad.CategoryId,
            ad.Title,
            ad.Description,
            ad.Price,
            ad.IsNegotiable,
            ad.CityId,
            ad.DistrictId,
            ad.District,
            ad.Region,
            ad.Type,
            ad.CreatedAt,
            ad.UpdatedAt,
            ad.IsDeleted,
            ModerationStatus = (isAdmin || isOwner) ? (ModerationStatus?)ad.ActualModerationStatus : null,
            Category = ad.Category,
            User = ad.User,
            Images = ad.Images,
            IsFavorite = isFavorite
        };

        return Ok(response);
    }

    // POST: /ads (multipart/form-data)
    [Authorize]
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromForm] Ad ad,
        [FromForm] List<IFormFile>? files,
        [FromForm] int? mainImageIndex)
    {
        if (string.IsNullOrWhiteSpace(ad.Title))
            return BadRequest(new ApiError("validation_error", "Title is required."));
        if (!ad.CategoryId.HasValue)
            return BadRequest(new ApiError("validation_error", "CategoryId is required."));

        if (!User.TryGetUserId(out var creatorId))
            return Unauthorized();

        ad.UserId = creatorId;
        ad.CreatedAt = DateTime.UtcNow;
        ad.UpdatedAt = DateTime.UtcNow;
        ad.IsDeleted = false;
        ad.ModerationStatus = ModerationStatus.Pending;

        db.Ads.Add(ad);
        await db.SaveChangesAsync();

        _logger.LogInformation("Ad created: id={AdId} by user={UserId}", ad.Id, creatorId);

        if (files?.Count > 0)
        {
            var savedImages = await SaveAdImages(ad.Id, files, mainImageIndex);
            // Возвращаем компактный ответ без циклических ссылок
            return Ok(new
            {
                Message = "Ad created with images successfully.",
                AdId = ad.Id,
                Images = savedImages.Select(img => new
                {
                    img.Id,
                    img.FilePath,
                    img.SortOrder,
                    img.IsMain
                })
            });
        }

        return Ok(new { Message = "Ad created successfully.", AdId = ad.Id });
    }

    // DELETE: /ads/5
    [Authorize]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ad = await db.Ads
            .Include(ad => ad.Images)
            .FirstOrDefaultAsync(ad => ad.Id == id && !ad.IsDeleted);
        if (ad == null)
            return NotFound();

        // Проверка авторства
        if (!User.TryGetUserId(out var currentUserId) || ad.UserId != currentUserId)
            return Forbid();

        ad.IsDeleted = true;
        ad.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _logger.LogInformation("Ad deleted: id={AdId} by user={UserId}", id, currentUserId);
        return Ok();
    }

    [Authorize]
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] Dictionary<string, object> data)
    {
        var ad = await db.Ads.Include(a => a.Images).FirstOrDefaultAsync(a => a.Id == id);
        if (ad == null)
            return NotFound();

        if (!User.TryGetUserId(out var currentUserId))
            return Unauthorized();

        // Проверка прав доступа
        if (!User.IsAdmin() && ad.UserId != currentUserId)
            return Forbid();

        var updated = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();

        using var transaction = await db.Database.BeginTransactionAsync();

        foreach (var kv in data)
        {
            var key = kv.Key;

            if (key.Equals("images", StringComparison.OrdinalIgnoreCase))
            {
                if (kv.Value is not JsonElement imagesElem || imagesElem.ValueKind != JsonValueKind.Array)
                {
                    errors.Add("images must be an array");
                    continue;
                }

                var images = JsonSerializer.Deserialize<List<JsonElement>>(imagesElem.GetRawText()) ?? new();
                foreach (var imageElem in images)
                {
                    try
                    {
                        if (imageElem.TryGetProperty("delete", out var deleteProp) && deleteProp.GetBoolean())
                        {
                            if (imageElem.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var idToDelete))
                            {
                                var imageToDelete = ad.Images.FirstOrDefault(img => img.Id == idToDelete);
                                if (imageToDelete != null)
                                {
                                    var filePath = imageToDelete.FilePath ?? string.Empty;
                                    var fullPath = Path.Combine("wwwroot", filePath);
                                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(fullPath))
                                    {
                                        System.IO.File.Delete(fullPath);
                                    }

                                    db.AdImages.Remove(imageToDelete);
                                    updated.Add($"Image {idToDelete} deleted");
                                }
                                else
                                {
                                    skipped.Add($"Image {idToDelete} not found");
                                }
                            }
                        }
                        else if (imageElem.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var idToUpdate))
                        {
                            var existingImage = ad.Images.FirstOrDefault(img => img.Id == idToUpdate);
                            if (existingImage != null)
                            {
                                if (imageElem.TryGetProperty("sortOrder", out var sortProp) && sortProp.TryGetInt32(out var sortOrder))
                                    existingImage.SortOrder = sortOrder;

                                if (imageElem.TryGetProperty("isMain", out var mainProp))
                                    existingImage.IsMain = mainProp.ValueKind == JsonValueKind.True;

                                updated.Add($"Image {idToUpdate} updated");
                            }
                            else
                            {
                                skipped.Add($"Image {idToUpdate} not found");
                            }
                        }
                        else if (imageElem.TryGetProperty("filePath", out var filePathProp))
                        {
                            var filePath = filePathProp.GetString();
                            if (string.IsNullOrEmpty(filePath))
                            {
                                skipped.Add("filePath is empty");
                                continue;
                            }

                            if (filePath.Contains("..") || Path.IsPathRooted(filePath))
                            {
                                skipped.Add($"Invalid filePath: {filePath}");
                                continue;
                            }

                            var fullPath = Path.Combine("wwwroot", filePath);
                            if (!System.IO.File.Exists(fullPath))
                            {
                                skipped.Add($"File not found: {filePath}");
                                continue;
                            }

                            var isMain = imageElem.TryGetProperty("isMain", out var mainProp) && mainProp.ValueKind == JsonValueKind.True;
                            var newImage = new AdImage
                            {
                                AdId = ad.Id,
                                FilePath = filePath,
                                SortOrder = imageElem.TryGetProperty("sortOrder", out var sortProp) && sortProp.TryGetInt32(out var sortOrder) ? sortOrder : 0,
                                IsMain = isMain
                            };

                            db.AdImages.Add(newImage);
                            updated.Add($"New image added: {filePath}");
                        }
                        else
                        {
                            skipped.Add($"Invalid image data: {imageElem.ToString()}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error processing image data: {ex.Message}");
                    }
                }

                continue;
            }

            // Обработка обычных свойств
            var prop = typeof(Ad).GetProperty(key, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite || !_allowedAdFields.Contains(prop.Name))
            {
                skipped.Add($"{key} (not allowed)");
                continue;
            }

            try
            {
                object value;
                if (kv.Value is JsonElement jsonElem)
                {
                    if (prop.PropertyType == typeof(decimal?) && jsonElem.ValueKind == JsonValueKind.String)
                    {
                        if (!decimal.TryParse(jsonElem.GetString(), out var parsedDecimal))
                        {
                            skipped.Add($"Invalid value for {key}: {jsonElem.GetString()}");
                            continue;
                        }
                        value = parsedDecimal;
                    }
                    else
                    {
                        value = JsonSerializer.Deserialize(jsonElem.GetRawText(), prop.PropertyType);
                        if (value == null)
                        {
                            skipped.Add($"Failed to deserialize property {key}.");
                            continue;
                        }
                    }
                }
                else
                {
                    value = Convert.ChangeType(kv.Value, prop.PropertyType);
                }

                prop.SetValue(ad, value);
                updated.Add(prop.Name);
            }
            catch (Exception ex)
            {
                skipped.Add($"Error processing property {key}: {ex.Message}");
            }
        }

        ad.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new
        {
            success = true,
            updated,
            skipped,
            errors
        });
    }

    [Authorize]
    [HttpGet("{id}/is-favorite")]
    public async Task<IActionResult> IsFavorite(int id)
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();
        var result = await db.UserFavoriteAds.AnyAsync(f => f.UserId == userId && f.AdId == id);
        return Ok(result);
    }

    private async Task<List<AdImage>> SaveAdImages(int adId, List<IFormFile> files, int? mainImageIndex)
    {
        if (!User.TryGetUserId(out var ownerId)) throw new UnauthorizedAccessException();
        var userIdStr = ownerId.ToString();
        var uploadPath = Path.Combine("wwwroot", "files", userIdStr, "Ads", adId.ToString());
        Directory.CreateDirectory(uploadPath);

        var savedImages = new List<AdImage>();

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var fileName = Path.GetRandomFileName() + Path.GetExtension(file.FileName);
            var fullPath = Path.Combine(uploadPath, fileName);

            await using var stream = file.OpenReadStream();
            await _imageService.SaveCompressedImageAsync(stream, uploadPath, fileName);

            var relativePath = Path.Combine("files", userIdStr, "Ads", adId.ToString(), fileName);

            var adImage = new AdImage
            {
                AdId = adId,
                FilePath = relativePath,
                SortOrder = i,
                IsMain = (mainImageIndex.HasValue && mainImageIndex.Value == i) || (!mainImageIndex.HasValue && i == 0)
            };

            db.AdImages.Add(adImage);
            savedImages.Add(adImage);
        }

        await db.SaveChangesAsync();
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

        if (!User.IsAdmin() && ad.UserId != uploadUserId)
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

        return Ok(new { files = uploadedPaths });
    }

    private Dictionary<string, IFilterHandler> BuildHandlers() =>
        new IFilterHandler[] {
            new SearchHandler(), new PriceHandler(), new DateHandler(),
            new IdHandler(), new LocationHandler(db, _cache),
            new MetaHandler(), new SimpleEqualityHandler()
        }
        .SelectMany(h => h.Types.Select(t => (type: t, handler: h)))
        .ToDictionary(x => x.type, x => x.handler, StringComparer.OrdinalIgnoreCase);
}