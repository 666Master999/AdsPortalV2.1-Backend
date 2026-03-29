using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using SixLabors.ImageSharp;

namespace AdsPortalV2.Controllers
{
    [ApiController]
    [Route("users")]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ImageService _imageService;
        private readonly IWebHostEnvironment _env;

        public UsersController(AppDbContext db, ImageService imageService, IWebHostEnvironment env)
        {
            _db = db;
            _imageService = imageService;
            _env = env;
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var user = await _db.Users
                .AsNoTracking()
                .Include(u => u.Ads)
                    .ThenInclude(ad => ad.Images)
                .Include(u => u.Sessions)
                .Include(u => u.Blocks)
                .Include(u => u.ReviewsReceived)
                .Include(u => u.ReviewsWritten)
                .Include(u => u.AdminLogs)
                .Include(u => u.ConversationsAsSeller)
                .Include(u => u.ConversationsAsBuyer)
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user is null)
            {
                return NotFound();
            }

            var currentUserId = User.FindFirst("id")?.Value;
            List<int>? currentUserFavorites = null;

            if (!string.IsNullOrEmpty(currentUserId) && int.TryParse(currentUserId, out var parsedCurrentUserId))
            {
                currentUserFavorites = await _db.UserFavoriteAds
                    .Where(fav => fav.UserId == parsedCurrentUserId)
                    .Select(fav => fav.Ad.Id)
                    .ToListAsync();
            }

            return Ok(new
            {
                UserProfile = new
                {
                    user.Id,
                    user.UserLogin,
                    user.UserName,
                    user.UserEmail,
                    user.UserPhoneNumber,
                    user.AvatarPath,
                    user.IsAdmin,
                    user.IsBlocked,
                    user.CreatedAt,
                    user.LastActivityAt,
                    Ads = user.Ads.Select(ad => new
                    {
                        ad.Id,
                        ad.Title,
                        ad.Description,
                        ad.Price,
                        ad.City,
                        ad.Type,
                        ad.IsNegotiable,
                        ad.CreatedAt,
                        ad.UpdatedAt,
                        ad.ViewsCount,
                        ad.FavoritesCount,
                        ad.ModerationStatus,
                        ad.IsDeleted,
                        ad.UserId,
                        Category = ad.Category == null ? null : new
                        {
                            ad.Category.Id,
                            ad.Category.Name
                        },
                        MainImage = ad.Images.FirstOrDefault(img => img.IsMain == true)?.FilePath,
                    }),
                    Sessions = user.Sessions,
                    Blocks = user.Blocks,
                    ReviewsReceived = user.ReviewsReceived,
                    ReviewsWritten = user.ReviewsWritten,
                    AdminLogs = user.AdminLogs
                },
                CurrentUserFavorites = currentUserFavorites
            });
        }

        [Authorize]
        [HttpPatch("{id:int}")]
        public async Task<IActionResult> Patch(int id, [FromBody] Dictionary<string, object> data)
        {
            if (!User.TryGetUserId(out var currentUserId))
                return Unauthorized();

            if (!await IsOwnerOrAdminAsync(currentUserId, id))
                return Forbid();

            var user = await _db.Users.FindAsync(id);
            if (user is null) return NotFound();

            var updated = new List<string>();
            var skipped = new List<string>();

            foreach (var kv in data)
            {
                var key = kv.Key;

                // 🔥 обработка пароля
                if (key.Equals("password", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = kv.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        user.UserPasswordHash = PasswordService.Hash(raw);
                        updated.Add("password");
                    }
                    else
                    {
                        skipped.Add("password (empty)");
                    }
                    continue;
                }

                // ищем свойство в User
                var prop = typeof(User).GetProperty(
                    key,
                    BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance
                );

                if (prop == null || !prop.CanWrite)
                {
                    skipped.Add($"{key} (no such property)");
                    continue;
                }

                // запрещённые поля
                if (prop.Name is "Id" or "IsAdmin" or "CreatedAt")
                {
                    skipped.Add($"{key} (protected)");
                    continue;
                }

                // универсальная конвертация JSON → тип свойства
                try
                {
                    var value = JsonSerializer.Deserialize(
                        JsonSerializer.Serialize(kv.Value),
                        prop.PropertyType
                    );

                    prop.SetValue(user, value);
                    updated.Add(prop.Name);
                }
                catch
                {
                    skipped.Add($"{key} (conversion failed)");
                }
            }

            await _db.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                updated,
                skipped
            });
        }

        [Authorize]
        [HttpGet("{id:int}/favorites")]
        public async Task<IActionResult> GetFavorites(int id)
        {
            if (!User.TryGetUserId(out var currentUserId) || currentUserId != id)
                return Forbid();

            var favorites = await _db.UserFavoriteAds
                .AsNoTracking()
                .Where(f => f.UserId == id)
                .Select(f => new { f.AdId, f.AddedAt })
                .ToListAsync();

            return Ok(favorites);
        }

        [Authorize]
        [HttpPost("{id:int}/favorites")]
        public async Task<IActionResult> AddToFavorites(int id, [FromBody] int adId)
        {
            if (!User.TryGetUserId(out var currentUserId) || currentUserId != id)
                return Forbid();

            var ad = await _db.Ads.FindAsync(adId);
            if (ad == null) return NotFound();

            var exists = await _db.UserFavoriteAds.AnyAsync(f => f.UserId == id && f.AdId == adId);
            if (exists) return Conflict(new { message = "Already in favorites." });

            _db.UserFavoriteAds.Add(new UserFavoriteAd { UserId = id, AdId = adId });
            ad.FavoritesCount++;
            await _db.SaveChangesAsync();

            return Ok(new { adId, addedAt = DateTime.UtcNow });
        }

        [Authorize]
        [HttpDelete("{id:int}/favorites/{adId:int}")]
        public async Task<IActionResult> RemoveFromFavorites(int id, int adId)
        {
            if (!User.TryGetUserId(out var currentUserId) || currentUserId != id)
                return Forbid();

            var favorite = await _db.UserFavoriteAds.FirstOrDefaultAsync(f => f.UserId == id && f.AdId == adId);
            if (favorite == null) return NotFound();

            var ad = await _db.Ads.FindAsync(adId);
            if (ad != null && ad.FavoritesCount > 0) ad.FavoritesCount--;

            _db.UserFavoriteAds.Remove(favorite);
            await _db.SaveChangesAsync();

            return Ok();
        }

        [HttpGet("{id:int}/ads")]
        public async Task<IActionResult> GetAds(int id)
        {
            var ads = await _db.Ads
                .AsNoTracking()
                .ToListAsync();

            return Ok(ads);
        }

        [HttpGet("userprofile/{id:int}")]
        public async Task<IActionResult> GetUserProfile(int id)
        {
            var currentUserId = User.TryGetUserId(out var uid) ? uid : 0;

            var profile = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == id)
                .Select(u => new
                {
                    u.Id,
                    u.UserName,
                    u.AvatarPath,
                    u.CreatedAt,
                    UserEmail = currentUserId == id ? u.UserEmail : null,
                    UserPhoneNumber = currentUserId == id ? u.UserPhoneNumber : null,
                    IsAdmin = currentUserId == id ? u.IsAdmin : (bool?)null,
                    IsBlocked = currentUserId == id ? u.IsBlocked : (bool?)null,
                    Ads = currentUserId == id ? u.Ads : null
                })
                .FirstOrDefaultAsync();

            return profile is null ? NotFound() : Ok(profile);
        }

        [Authorize]
        [HttpPost("{id:int}/upload-avatar")]
        public async Task<IActionResult> UploadAvatar(int id, IFormFile avatar)
        {
            if (!User.TryGetUserId(out var currentUserId)) return Unauthorized();
            if (!await IsOwnerOrAdminAsync(currentUserId, id)) return Forbid();

            var user = await _db.Users.FindAsync(id);
            if (user is null) return NotFound();
            if (avatar == null || avatar.Length == 0) return BadRequest(new { message = "No file uploaded." });

            var webRoot = _env.WebRootPath ?? "wwwroot";
            var uploadsFolder = Path.Combine(webRoot, "files", id.ToString(), "Avatars");
            Directory.CreateDirectory(uploadsFolder);

            // Удаляем предыдущий аватар, если он есть (убираем query string ?v=...)
            if (!string.IsNullOrWhiteSpace(user.AvatarPath))
            {
                var avatarUrl = user.AvatarPath.Split('?')[0].TrimStart('/');
                var prevFullPath = Path.Combine(webRoot, avatarUrl.Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(prevFullPath))
                {
                    try { System.IO.File.Delete(prevFullPath); } catch { /* игнорируем ошибки удаления */ }
                }
            }

            using var ms = new MemoryStream();
            await avatar.CopyToAsync(ms);
            ms.Position = 0;

            // фиксированное имя, перезаписывается каждый раз
            var avatarFileName = "avatar.jpg";
            ms.Position = 0;
            await _imageService.SaveCompressedImageAsync(ms, uploadsFolder, avatarFileName, targetKb: 100, minQuality: 1);

            // кеш‑бастер, чтобы клиент видел обновление
            var version = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            user.AvatarPath = $"/files/{id}/Avatars/{avatarFileName}?v={version}";
            await _db.SaveChangesAsync();

            return Ok(new { avatarPath = user.AvatarPath });
        }


        // --- Helpers ---

        // Проверка: владелец ресурса или админ.
        private Task<bool> IsOwnerOrAdminAsync(int currentUserId, int resourceOwnerId) =>
            Task.FromResult(currentUserId == resourceOwnerId || User.IsAdmin());
    }

    // --- ClaimsPrincipal extensions ---
    public static class ClaimsPrincipalExtensions
    {
        public static bool TryGetUserId(this ClaimsPrincipal user, out int userId)
        {
            userId = 0;
            if (user == null) return false;

            var idValue = user.FindFirstValue("id") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(idValue)) return false;
            return int.TryParse(idValue, out userId);
        }

        public static bool IsAdmin(this ClaimsPrincipal user) =>
            user.FindFirstValue("isAdmin") == "True";
    }
}
