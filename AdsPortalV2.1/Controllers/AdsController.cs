using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.Json;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("ads")]
public class AdsController(AppDbContext db, ImageService _imageService) : ControllerBase
{
    // PATCH: /ads/{id}/moderation
    [Authorize]
    [HttpPatch("{id}/moderation")]
    public async Task<IActionResult> PatchModerationStatus(int id, [FromBody] ModerationStatus status)
    {
        var userIdClaim = User.FindFirst("id")?.Value;
        if (userIdClaim == null)
            return Unauthorized();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == int.Parse(userIdClaim));
        if (user == null || !user.IsAdmin)
            return Forbid();

        var ad = await db.Ads.FirstOrDefaultAsync(a => a.Id == id);
        if (ad == null)
            return NotFound();

        ad.ModerationStatus = status;
        ad.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { ad.Id, ad.ModerationStatus });
    }
    // GET: /ads/moderation
    [Authorize]
    [HttpGet("moderation")]
    public async Task<IActionResult> GetModerationList()
    {
        var userIdClaim = User.FindFirst("id")?.Value;
        if (userIdClaim == null)
            return Unauthorized();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == int.Parse(userIdClaim));
        if (user == null || !user.IsAdmin)
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
    public async Task<IActionResult> GetAll([FromQuery] string? sortBy, [FromQuery] bool descending = false, [FromQuery] int? categoryId = null)
    {
        IQueryable<Ad> query;
        var userIdClaim = User.FindFirst("id")?.Value;
        bool isAdmin = false;
        if (userIdClaim != null)
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == int.Parse(userIdClaim));
            isAdmin = user?.IsAdmin == true;
        }

        if (isAdmin)
        {
            query = db.Ads;
        }
        else
        {
            var userId = userIdClaim != null ? int.Parse(userIdClaim) : (int?)null;
            query = db.Ads.Where(ad => !ad.IsDeleted && (ad.ModerationStatus == ModerationStatus.Approved || (userId.HasValue && ad.UserId == userId.Value)));
        }

        if (categoryId.HasValue)
            query = query.Where(ad => ad.CategoryId == categoryId.Value);

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
            ad.CategoryId,
            ad.City,
            ad.Type,
            ad.CreatedAt,
            ad.UpdatedAt,
            ad.UserId,
            MainImageUrl = ad.Images
                .Where(img => img.IsMain)
                .Select(img => img.FilePath)
                .FirstOrDefault()
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
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            isAdmin = user?.IsAdmin == true;
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
                a.City,
                a.Type,
                a.CreatedAt,
                a.UpdatedAt,
                a.IsDeleted,
                ActualModerationStatus = a.ModerationStatus, // всегда получаем реальный статус
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
                    a.User.CreatedAt
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

        // Формируем ответ, скрывая статус модерации от посторонних
        var response = new
        {
            ad.Id,
            ad.UserId,
            ad.CategoryId,
            ad.Title,
            ad.Description,
            ad.Price,
            ad.City,
            ad.Type,
            ad.CreatedAt,
            ad.UpdatedAt,
            ad.IsDeleted,
            ModerationStatus = (isAdmin || isOwner) ? (ModerationStatus?)ad.ActualModerationStatus : null,
            Category = ad.Category,
            User = ad.User,
            Images = ad.Images
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
        if (userIdClaim == null || ad.UserId != int.Parse(userIdClaim))
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
                // Список для отслеживания обработанных ID (если нужно)
                var processedImageIds = new HashSet<int>();

                foreach (var imageElem in images)
                {
                    try
                    {
                        // Удаление
                        if (imageElem.TryGetProperty("delete", out var deleteProp) && deleteProp.GetBoolean())
                        {
                            if (imageElem.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var idToDelete))
                            {
                                var imageToDelete = ad.Images.FirstOrDefault(img => img.Id == idToDelete);
                                if (imageToDelete != null)
                                {
                                    var fullPath = Path.Combine("wwwroot", imageToDelete.FilePath);
                                    if (System.IO.File.Exists(fullPath))
                                        System.IO.File.Delete(fullPath);

                                    db.AdImages.Remove(imageToDelete);
                                    updated.Add($"Image {idToDelete} deleted");
                                }
                                else
                                {
                                    skipped.Add($"Image {idToDelete} not found");
                                }
                            }
                        }
                        // Обновление существующего
                        else if (imageElem.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var idToUpdate))
                        {
                            var existingImage = ad.Images.FirstOrDefault(img => img.Id == idToUpdate);
                            if (existingImage != null)
                            {
                                if (imageElem.TryGetProperty("sortOrder", out var sortProp) && sortProp.TryGetInt32(out var sortOrder))
                                    existingImage.SortOrder = sortOrder;

                                if (imageElem.TryGetProperty("isMain", out var mainProp))
                                    existingImage.IsMain = mainProp.ValueKind == JsonValueKind.True;

                                processedImageIds.Add(idToUpdate);
                                updated.Add($"Image {idToUpdate} updated");
                            }
                            else
                            {
                                skipped.Add($"Image {idToUpdate} not found");
                            }
                        }
                        // Добавление нового
                        else if (imageElem.TryGetProperty("filePath", out var filePathProp))
                        {
                            var filePath = filePathProp.GetString();
                            if (string.IsNullOrEmpty(filePath))
                            {
                                skipped.Add("filePath is empty");
                                continue;
                            }

                            // Проверка безопасности пути
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

                var allImages = ad.Images.ToList();

                var existingImageIds = ad.Images.Select(i => i.Id).ToHashSet();
                var newImages = db.AdImages.Local.Where(i => i.AdId == ad.Id && i.Id == 0).ToList(); // новые, ещё не сохранённые
                var allImagesForSort = ad.Images.Concat(newImages).OrderBy(i => i.SortOrder).ToList();

                for (int i = 0; i < allImagesForSort.Count; i++)
                {
                    allImagesForSort[i].SortOrder = i;
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
                    value = JsonSerializer.Deserialize(jsonElem.GetRawText(), prop.PropertyType);
                }
                else
                {
                    // fallback для простых типов (int, string и т.д.) если вдруг не JsonElement
                    value = Convert.ChangeType(kv.Value, prop.PropertyType);
                }

                prop.SetValue(ad, value);
                updated.Add(prop.Name);
            }
            catch (Exception ex)
            {
                errors.Add($"Error processing property {key}: {ex.Message}");
            }
        }

        ad.UpdatedAt = DateTime.UtcNow;

        // Если есть ошибки, откатываем транзакцию и возвращаем BadRequest
        if (errors.Any())
        {
            await transaction.RollbackAsync();
            return BadRequest(new { success = false, errors, skipped, updated });
        }

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
        if (userIdClaim == null || ad.UserId != int.Parse(userIdClaim))
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