using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;

namespace AdsPortalV2.Controllers
{
    [ApiController]
    [Route("users")]
    public class UsersController : ControllerBase
    {
        private static readonly HashSet<string> _allowedUserFields = ["UserLogin", "UserName", "UserEmail", "UserPhoneNumber", "AvatarPath"];

        private readonly AppDbContext _db;
        private readonly ImageService _imageService;
        private readonly IWebHostEnvironment _env;
        private readonly OnlineUserTracker _tracker;

        public UsersController(AppDbContext db, ImageService imageService, IWebHostEnvironment env, OnlineUserTracker tracker)
        {
            _db = db;
            _imageService = imageService;
            _env = env;
            _tracker = tracker;
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var user = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == id)
                .Select(user => new
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
                        ad.CityId,
                        ad.DistrictId,
                        City = ad.CityRef == null ? null : new LocationRef("city", ad.CityRef.Id, ad.CityRef.Name),
                        District = ad.District == null ? null : new LocationRef("district", ad.District.Id, ad.District.Name),
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
                        MainImage = ad.Images.Where(img => img.IsMain).Select(img => img.FilePath).FirstOrDefault(),
                    })
                })
                .FirstOrDefaultAsync();

            if (user is null)
                return NotFound();

            List<int>? currentUserFavorites = null;
            if (User.TryGetUserId(out var currentUserId))
            {
                currentUserFavorites = await _db.UserFavoriteAds
                    .Where(fav => fav.UserId == currentUserId)
                    .Select(fav => fav.AdId)
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
                    IsOnline = _tracker.IsOnline(user.Id),
                    user.CreatedAt,
                    user.LastActivityAt,
                    user.Ads
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

            if (!IsOwnerOrAdmin(currentUserId, id))
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

                if (prop == null || !prop.CanWrite || !_allowedUserFields.Contains(prop.Name))
                {
                    skipped.Add($"{key} (not allowed)");
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
                .Join(_db.Ads, f => f.AdId, a => a.Id, (f, a) => new { Ad = a, f.AddedAt })
                .Select(x => new
                {
                    id = x.Ad.Id,
                    title = x.Ad.Title,
                    price = x.Ad.Price,
                    isNegotiable = x.Ad.IsNegotiable,
                    mainImage = x.Ad.Images.Where(img => img.IsMain).Select(img => img.FilePath).FirstOrDefault(),
                    mainImageUrl = x.Ad.Images.Where(img => img.IsMain).Select(img => img.FilePath).FirstOrDefault(),
                    createdAt = x.Ad.CreatedAt,
                    updatedAt = x.Ad.UpdatedAt,
                    cityRef = x.Ad.CityRef == null ? null : new LocationRef("city", x.Ad.CityRef.Id, x.Ad.CityRef.Name),
                    districtRef = x.Ad.District == null ? null : new LocationRef("district", x.Ad.District.Id, x.Ad.District.Name),
                    viewsCount = x.Ad.ViewsCount,
                    favoritesCount = x.Ad.FavoritesCount,
                    moderationStatus = x.Ad.ModerationStatus,
                    status = x.Ad.ModerationStatus,
                    isFavorite = true,
                    userId = x.Ad.UserId
                })
                .ToListAsync();

            return Ok(favorites);
        }

        [Authorize]
        [HttpPost("{id:int}/favorites")]
        public async Task<IActionResult> AddToFavorites(int id, [FromBody] int adId)
        {
            if (!User.TryGetUserId(out var currentUserId) || currentUserId != id)
                return Forbid();

            if (!await _db.Ads.AnyAsync(a => a.Id == adId)) return NotFound();

            var exists = await _db.UserFavoriteAds.AnyAsync(f => f.UserId == id && f.AdId == adId);
            if (exists) return Conflict(new ApiError("conflict", "Already in favorites."));

            _db.UserFavoriteAds.Add(new UserFavoriteAd { UserId = id, AdId = adId });
            await _db.SaveChangesAsync();
            await _db.Ads.Where(a => a.Id == adId).ExecuteUpdateAsync(s => s.SetProperty(a => a.FavoritesCount, a => a.FavoritesCount + 1));

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

            _db.UserFavoriteAds.Remove(favorite);
            await _db.SaveChangesAsync();
            await _db.Ads.Where(a => a.Id == adId && a.FavoritesCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.FavoritesCount, a => a.FavoritesCount - 1));

            return Ok();
        }

        [HttpGet("{id:int}/ads")]
        public async Task<IActionResult> GetAds(int id)
        {
            var ads = await _db.Ads
                .AsNoTracking()
                .Where(a => a.UserId == id)
                .Select(ad => new
                {
                    ad.Id,
                    ad.Title,
                    ad.Description,
                    ad.Price,
                    ad.CityId,
                    ad.DistrictId,
                    City = ad.CityRef == null ? null : new LocationRef("city", ad.CityRef.Id, ad.CityRef.Name),
                    District = ad.District == null ? null : new LocationRef("district", ad.District.Id, ad.District.Name),
                    ad.Type,
                    ad.IsNegotiable,
                    ad.CreatedAt,
                    ad.UpdatedAt,
                    ad.ViewsCount,
                    ad.FavoritesCount,
                    ad.ModerationStatus,
                    ad.IsDeleted,
                    ad.UserId,
                    Category = ad.Category == null ? null : new { ad.Category.Id, ad.Category.Name },
                    MainImage = ad.Images.Where(img => img.IsMain).Select(img => img.FilePath).FirstOrDefault()
                })
                .ToListAsync();

            return Ok(ads);
        }

        [HttpGet("userprofile/{id:int}")]
        public async Task<IActionResult> GetUserProfile(int id)
        {
            var currentUserId = User.TryGetUserId(out var uid) ? uid : 0;
            var isOwner = currentUserId == id;

            var profile = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == id)
                .Select(u => new
                {
                    u.Id,
                    u.UserName,
                    u.AvatarPath,
                    u.CreatedAt,
                    UserEmail = isOwner ? u.UserEmail : null,
                    UserPhoneNumber = isOwner ? u.UserPhoneNumber : null,
                    IsAdmin = isOwner ? u.IsAdmin : (bool?)null,
                    IsBlocked = isOwner ? u.IsBlocked : (bool?)null,
                    Ads = isOwner ? u.Ads.Select(ad => new
                    {
                        ad.Id,
                        ad.Title,
                        ad.Description,
                        ad.Price,
                        ad.CityId,
                        ad.DistrictId,
                        City = ad.CityRef == null ? null : new LocationRef("city", ad.CityRef.Id, ad.CityRef.Name),
                        District = ad.District == null ? null : new LocationRef("district", ad.District.Id, ad.District.Name),
                        ad.Type,
                        ad.IsNegotiable,
                        ad.CreatedAt,
                        ad.UpdatedAt,
                        ad.ViewsCount,
                        ad.FavoritesCount,
                        ad.ModerationStatus,
                        ad.IsDeleted,
                        ad.UserId,
                        Category = ad.Category == null ? null : new { ad.Category.Id, ad.Category.Name },
                        MainImage = ad.Images.Where(img => img.IsMain).Select(img => img.FilePath).FirstOrDefault()
                    }) : null
                })
                .FirstOrDefaultAsync();

            return profile is null ? NotFound() : Ok(profile);
        }

        [Authorize]
        [HttpPost("{id:int}/upload-avatar")]
        public async Task<IActionResult> UploadAvatar(int id, IFormFile avatar)
        {
            if (!User.TryGetUserId(out var currentUserId)) return Unauthorized();
            if (!IsOwnerOrAdmin(currentUserId, id)) return Forbid();

            var user = await _db.Users.FindAsync(id);
            if (user is null) return NotFound();
            if (avatar == null || avatar.Length == 0) return BadRequest(new ApiError("validation_error", "No file uploaded."));

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

            var avatarFileName = "avatar.jpg";
            await _imageService.SaveCompressedImageAsync(ms, uploadsFolder, avatarFileName, targetKb: 100, minQuality: 1);

            // кеш‑бастер, чтобы клиент видел обновление
            var version = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            user.AvatarPath = $"/files/{id}/Avatars/{avatarFileName}?v={version}";
            await _db.SaveChangesAsync();

            return Ok(new { avatarPath = user.AvatarPath });
        }


        // --- Helpers ---

        // Проверка: владелец ресурса или админ.
        private bool IsOwnerOrAdmin(int currentUserId, int resourceOwnerId) =>
            currentUserId == resourceOwnerId || User.IsAdmin();
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
