using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Hubs;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Reflection;
using System.Text.Json;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("ads")]
public class AdsController(AppDbContext db, ImageService _imageService, IMemoryCache _cache, IHubContext<NotificationHub> _hub) : ControllerBase
{
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
                ad.City,
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
    // GET: /ads?sortBy=title&descending=false
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? sortBy, [FromQuery] bool descending = false, [FromQuery] int? categoryId = null, [FromQuery] List<int>? ids = null)
    {
        IQueryable<Ad> query;
        var userIdClaim = User.FindFirst("id")?.Value;
        bool isAdmin = false;
        int? userId = null;

        if (userIdClaim != null && int.TryParse(userIdClaim, out var parsedUserId))
        {
            userId = parsedUserId;
            isAdmin = User.IsAdmin();
        }

        if (isAdmin)
        {
            query = db.Ads;
        }
        else
        {
            query = db.Ads.Where(ad => !ad.IsDeleted && (ad.ModerationStatus == ModerationStatus.Approved || (userId.HasValue && ad.UserId == userId.Value)));
        }

        if (categoryId.HasValue)
            query = query.Where(ad => ad.CategoryId == categoryId.Value);

        if (ids?.Count > 0)
            query = query.Where(ad => ids.Contains(ad.Id));

        query = sortBy?.ToLower() switch
        {
            "title" => descending ? query.OrderByDescending(ad => ad.Title) : query.OrderBy(ad => ad.Title),
            "price" => descending ? query.OrderByDescending(ad => ad.Price) : query.OrderBy(ad => ad.Price),
            "created" => descending ? query.OrderByDescending(ad => ad.CreatedAt) : query.OrderBy(ad => ad.CreatedAt),
            _ => query.OrderByDescending(ad => ad.CreatedAt)
        };

        var result = await query.Select(ad => new
        {
            ad.Id,
            ad.Title,
            ad.Description,
            ad.Price,
            ad.IsNegotiable,
            ad.CategoryId,
            ad.City,
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
            IsFavorite = userId.HasValue && db.UserFavoriteAds.Any(fav => fav.UserId == userId.Value && fav.AdId == ad.Id),
            ModerationStatus = isAdmin || (userId.HasValue && ad.UserId == userId.Value) ? (ModerationStatus?)ad.ModerationStatus : null
        }).ToListAsync();

        return Ok(result);
    }

    // GET: /ads/5
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        // Получаем текущего пользователя
        var userIdClaim = User.FindFirst("id")?.Value;
        int? userId = null;
        bool isAdmin = false;

        if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out var parsedUserId))
        {
            userId = parsedUserId;
            isAdmin = User.IsAdmin();
        }

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
                a.City,
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
            return NotFound(new { Message = "Объявление не найдено." });

        // Проверка доступа на основе реальных данных
        bool isOwner = userId.HasValue && ad.UserId == userId.Value;
        if (!isAdmin && !isOwner)
        {
            if (ad.IsDeleted || ad.ActualModerationStatus != ModerationStatus.Approved)
                return new ObjectResult(new { Message = "У вас нет прав доступа к этому объявлению." }) { StatusCode = 403 };
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

        // Получение ID избранных объявлений текущего пользователя
        List<int>? currentUserFavorites = null;
        if (userId.HasValue)
        {
            currentUserFavorites = await db.UserFavoriteAds
                .Where(fav => fav.UserId == userId.Value)
                .Select(fav => fav.AdId)
                .ToListAsync();
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
            ad.IsNegotiable, // Добавляем флаг договорной цены
            ad.City,
            ad.Type,
            ad.CreatedAt,
            ad.UpdatedAt,
            ad.IsDeleted,
            ModerationStatus = (isAdmin || isOwner) ? (ModerationStatus?)ad.ActualModerationStatus : null,
            Category = ad.Category,
            User = ad.User,
            Images = ad.Images,
            CurrentUserFavorites = currentUserFavorites
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
            return BadRequest("Title is required.");
        if (!ad.CategoryId.HasValue)
            return BadRequest("CategoryId is required.");

        var userIdClaim = User.FindFirst("id")?.Value;
        if (userIdClaim == null)
            return Unauthorized();

        ad.UserId = int.Parse(userIdClaim);
        ad.CreatedAt = DateTime.UtcNow;
        ad.UpdatedAt = DateTime.UtcNow;
        ad.IsDeleted = false;
        ad.ModerationStatus = ModerationStatus.Pending;

        db.Ads.Add(ad);
        await db.SaveChangesAsync();

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
        var userIdClaim = User.FindFirst("id")?.Value;
        if (userIdClaim == null || ad.UserId != int.Parse(userIdClaim))
            return Forbid();

        ad.IsDeleted = true;
        ad.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok();
    }

    [Authorize]
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] Dictionary<string, object> data)
    {
        var ad = await db.Ads.Include(a => a.Images).FirstOrDefaultAsync(a => a.Id == id);
        if (ad == null)
            return NotFound();

        var userIdClaim = User.FindFirst("id")?.Value;
        if (userIdClaim == null)
            return Unauthorized();

        // Проверка прав доступа
        if (!User.IsAdmin() && ad.UserId != int.Parse(userIdClaim))
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
            if (prop == null || !prop.CanWrite)
            {
                skipped.Add($"{key} (no such property)");
                continue;
            }

            if (prop.Name is "Id" or "UserId" or "CreatedAt")
            {
                skipped.Add($"{key} (protected)");
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

    private async Task<List<AdImage>> SaveAdImages(int adId, List<IFormFile> files, int? mainImageIndex)
    {
        var userId = User.FindFirst("id")?.Value ?? throw new UnauthorizedAccessException();
        var uploadPath = Path.Combine("wwwroot", "files", userId, "Ads", adId.ToString());
        Directory.CreateDirectory(uploadPath);

        var savedImages = new List<AdImage>();

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var fileName = Path.GetRandomFileName() + Path.GetExtension(file.FileName);
            var fullPath = Path.Combine(uploadPath, fileName);

            await using var stream = file.OpenReadStream();
            await _imageService.SaveCompressedImageAsync(stream, uploadPath, fileName);

            var relativePath = Path.Combine("files", userId, "Ads", adId.ToString(), fileName);

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

        var userIdClaim = User.FindFirst("id")?.Value;
        if (userIdClaim == null)
            return Unauthorized();

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == int.Parse(userIdClaim));
        if (user == null)
            return Unauthorized();

        // Проверка прав доступа
        if (!user.IsAdmin && ad.UserId != int.Parse(userIdClaim))
            return Forbid();

        if (files == null || files.Count == 0)
            return BadRequest("No files");

        var uploadDir = Path.Combine("wwwroot", "files", userIdClaim, "Ads", id.ToString());
        Directory.CreateDirectory(uploadDir);

        var uploadedPaths = new List<string>();

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var ext = Path.GetExtension(file.FileName);
            var fileName = _imageService.GenerateShortFileName(ext);
            await using var stream = file.OpenReadStream();
            await _imageService.SaveCompressedImageAsync(stream, uploadDir, fileName);

            var relativePath = Path.Combine("files", userIdClaim, "Ads", id.ToString(), fileName).Replace('\\', '/');
            uploadedPaths.Add(relativePath);
        }

        return Ok(new { files = uploadedPaths });
    }
}