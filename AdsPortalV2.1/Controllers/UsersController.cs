using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace AdsPortalV2.Controllers
{
    [ApiController]
    [Route("users")]
    public class UsersController(AppDbContext db, ImageService imageService, IWebHostEnvironment env, OnlineUserTracker tracker, AdVisibilityService adVisibility) : ControllerBase
    {
        private readonly AppDbContext _db = db;
        private readonly ImageService _imageService = imageService;
        private readonly IWebHostEnvironment _env = env;
        private readonly OnlineUserTracker _tracker = tracker;
        private readonly AdVisibilityService _adVisibility = adVisibility;

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var currentUserId = User.TryGetUserId(out var currentUid) ? currentUid : (int?)null;
            if (currentUserId.HasValue && await IsBlockedBidirectionalAsync(currentUserId.Value, id))
                return NotFound(new ApiError("not_found", "Пользователь заблокирован или не найден"));

            var user = await _db.Users
                .AsNoTracking()
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user is null)
                return NotFound();

            var visibleAdsQuery = await _adVisibility.ApplyVisibilityAsync(
                _db.Ads.AsNoTracking()
                    .Include(a => a.Location)
                    .Include(a => a.Category)
                    .Include(a => a.Images)
                    .Where(a => a.UserId == id),
                currentUserId);

            bool isOwnProfile = currentUserId.HasValue && currentUserId.Value == id;

            var ads = await visibleAdsQuery.Select(ad => new UserAdDto(
                ad.Id,
                ad.Title,
                ad.Description,
                ad.Price,
                ad.LocationId,
                ad.Location == null ? null : new LocationRef(ad.Location.Type, ad.Location.Id, ad.Location.Name),
                ad.ListingType,
                ad.IsNegotiable,
                ad.CreatedAt,
                ad.UpdatedAt,
                ad.ViewsCount,
                ad.FavoritesCount,
                ad.Status,
                ad.RejectionReason,
                ad.DeletedAt,
                ad.UserId,
                ad.Category == null ? null : new AdCategoryDto(ad.Category.Id, ad.Category.Name),
                _db.AdImages.Where(img => img.Id == ad.MainImageId).Select(img => img.FilePath).FirstOrDefault()))
            .ToListAsync();

            if (!isOwnProfile)
                ads = [.. ads.Select(a => a with { ModerationStatus = null })];

            List<int>? currentUserFavorites = null;
            if (User.TryGetUserId(out var currentUserIdForFavorites))
            {
                currentUserFavorites = await _db.UserFavoriteAds
                    .Where(fav => fav.UserId == currentUserIdForFavorites)
                    .Select(fav => fav.AdId)
                    .ToListAsync();

                var favSet = currentUserFavorites.ToHashSet();
                if (favSet.Count > 0)
                    ads = [.. ads.Select(a => a with { IsFavorite = favSet.Contains(a.Id) })];
            }

            var dto = new UserProfileResponseDto(
                new UserProfileDto(
                    user.Id,
                    user.UserLogin,
                    user.UserName,
                    user.UserEmail,
                    user.UserPhoneNumber,
                    user.AvatarPath,
                    [.. user.UserRoles.Select(ur => ur.Role?.Name).OfType<string>()],
                    _tracker.IsOnline(user.Id),
                    user.CreatedAt,
                    user.LastActivityAt,
                    ads),
                currentUserFavorites);

            return Ok(dto);
        }

        [Authorize]
        [HttpPatch("{id:int}")]
        public async Task<IActionResult> Patch(int id, [FromBody] Dictionary<string, JsonElement> data)
        {
            if (!User.TryGetUserId(out var currentUserId))
                return Unauthorized();

            if (!IsOwnerOrAdmin(currentUserId, id))
                return Forbid();

            var user = await _db.Users.FindAsync(id);
            if (user is null) return NotFound();

            var updated = new HashSet<string>();
            var skipped = new List<string>();
            var errors = new List<PatchErrorDto>();

            foreach (var kv in data)
            {
                var key = kv.Key;

                if (key.Equals(UserFieldNames.Password, StringComparison.OrdinalIgnoreCase))
                {
                    var password = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : null;
                    if (string.IsNullOrWhiteSpace(password))
                        errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, UserFieldNames.Password, "Password cannot be empty."));
                    else
                    {
                        user.UserPasswordHash = PasswordService.Hash(password);
                        updated.Add(UserFieldNames.Password);
                    }

                    continue;
                }

                if (key.Equals(UserFieldNames.UserLogin, StringComparison.OrdinalIgnoreCase)) PatchHelpers.UpdateString(kv.Value, user.UserLogin, v => user.UserLogin = v, UserFieldNames.UserLogin, updated, skipped, errors, required: true);
                else if (key.Equals(UserFieldNames.UserName, StringComparison.OrdinalIgnoreCase)) PatchHelpers.UpdateNullableString(kv.Value, user.UserName, v => user.UserName = v, UserFieldNames.UserName, updated, skipped, errors);
                else if (key.Equals(UserFieldNames.UserEmail, StringComparison.OrdinalIgnoreCase)) PatchHelpers.UpdateNullableString(kv.Value, user.UserEmail, v => user.UserEmail = v, UserFieldNames.UserEmail, updated, skipped, errors);
                else if (key.Equals(UserFieldNames.UserPhoneNumber, StringComparison.OrdinalIgnoreCase)) PatchHelpers.UpdateNullableString(kv.Value, user.UserPhoneNumber, v => user.UserPhoneNumber = v, UserFieldNames.UserPhoneNumber, updated, skipped, errors);
                else if (key.Equals(UserFieldNames.AvatarPath, StringComparison.OrdinalIgnoreCase)) PatchHelpers.UpdateNullableString(kv.Value, user.AvatarPath, v => user.AvatarPath = v, UserFieldNames.AvatarPath, updated, skipped, errors);
                else errors.Add(new PatchErrorDto(PatchErrorCodes.NotAllowed, key, $"Field '{key}' is not allowed."));
            }

            await _db.SaveChangesAsync();

            var success = errors.Count == 0 || updated.Count > 0;
            return success
                ? Ok(new PatchResultDto(true, updated, skipped, errors))
                : BadRequest(new PatchResultDto(false, updated, skipped, errors));
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
                .Select(x => new FavoriteAdDto(
                    x.Ad.Id,
                    x.Ad.Title,
                    x.Ad.Price,
                    x.Ad.IsNegotiable,
                    _db.AdImages.Where(img => img.Id == x.Ad.MainImageId).Select(img => img.FilePath).FirstOrDefault(),
                    x.Ad.CreatedAt,
                    x.Ad.UpdatedAt,
                    x.Ad.Location == null ? null : new LocationRef(x.Ad.Location.Type, x.Ad.Location.Id, x.Ad.Location.Name),
                    x.Ad.ViewsCount,
                    x.Ad.FavoritesCount,
                    (AdStatus)x.Ad.Status,
                    true,
                    x.Ad.UserId))
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

            return Ok(new FavoriteMutationDto(adId, DateTime.UtcNow));
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
            var currentUserId = User.TryGetUserId(out var uid) ? uid : (int?)null;
            if (currentUserId.HasValue && await IsBlockedBidirectionalAsync(currentUserId.Value, id))
                return NotFound(new ApiError("not_found", "Пользователь заблокирован или не найден"));

            var baseQuery = _db.Ads.AsNoTracking()
                .Include(a => a.Location)
                .Include(a => a.Category)
                .Where(a => a.UserId == id);

            var visibleQuery = await _adVisibility.ApplyVisibilityAsync(baseQuery, currentUserId);

            bool isOwnProfile = currentUserId.HasValue && currentUserId.Value == id;

            var ads = await visibleQuery.Select(ad => new UserAdDto(
                ad.Id,
                ad.Title,
                ad.Description,
                ad.Price,
                ad.LocationId,
                ad.Location == null ? null : new LocationRef(ad.Location.Type, ad.Location.Id, ad.Location.Name),
                ad.ListingType,
                ad.IsNegotiable,
                ad.CreatedAt,
                ad.UpdatedAt,
                ad.ViewsCount,
                ad.FavoritesCount,
                ad.Status,
                ad.RejectionReason,
                ad.DeletedAt,
                ad.UserId,
                ad.Category == null ? null : new AdCategoryDto(ad.Category.Id, ad.Category.Name),
                _db.AdImages.Where(img => img.Id == ad.MainImageId).Select(img => img.FilePath).FirstOrDefault()))
            .ToListAsync();

            if (!isOwnProfile)
                ads = [.. ads.Select(a => a with { ModerationStatus = null })];

            if (currentUserId.HasValue)
            {
                var adIds = ads.Select(a => a.Id).ToList();
                var favSet = await _db.UserFavoriteAds
                    .Where(f => f.UserId == currentUserId.Value && adIds.Contains(f.AdId))
                    .Select(f => f.AdId).ToHashSetAsync();
                if (favSet.Count > 0)
                    ads = [.. ads.Select(a => a with { IsFavorite = favSet.Contains(a.Id) })];
            }

            return Ok(ads);
        }

        [HttpGet("userprofile/{id:int}")]
        public async Task<IActionResult> GetUserProfile(int id)
        {
            var currentUserId = User.TryGetUserId(out var uid) ? uid : 0;
            var isOwner = currentUserId == id;

            if (currentUserId > 0 && await IsBlockedBidirectionalAsync(currentUserId, id))
                return NotFound(new ApiError("not_found", "Пользователь заблокирован или не найден"));

            var user = await _db.Users
                .AsNoTracking()
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user is null) return NotFound();

            var adsQuery = _db.Ads.AsNoTracking()
                .Include(a => a.Location)
                .Include(a => a.Category)
                .Where(a => a.UserId == id);

            var visibleAdsQuery = await _adVisibility.ApplyVisibilityAsync(adsQuery, currentUserId == 0 ? null : currentUserId);
            var ads = await visibleAdsQuery.Select(ad => new UserAdDto(
                ad.Id,
                ad.Title,
                ad.Description,
                ad.Price,
                ad.LocationId,
                ad.Location == null ? null : new LocationRef(ad.Location.Type, ad.Location.Id, ad.Location.Name),
                ad.ListingType,
                ad.IsNegotiable,
                ad.CreatedAt,
                ad.UpdatedAt,
                ad.ViewsCount,
                ad.FavoritesCount,
                ad.Status,
                ad.RejectionReason,
                ad.DeletedAt,
                ad.UserId,
                ad.Category == null ? null : new AdCategoryDto(ad.Category.Id, ad.Category.Name),
                _db.AdImages.Where(img => img.Id == ad.MainImageId).Select(img => img.FilePath).FirstOrDefault())).ToListAsync();

            if (!isOwner)
                ads = [.. ads.Select(a => a with { ModerationStatus = null })];

            if (currentUserId > 0)
            {
                var adIds = ads.Select(a => a.Id).ToList();
                var favSet = await _db.UserFavoriteAds
                    .Where(f => f.UserId == currentUserId && adIds.Contains(f.AdId))
                    .Select(f => f.AdId).ToHashSetAsync();
                if (favSet.Count > 0)
                    ads = [.. ads.Select(a => a with { IsFavorite = favSet.Contains(a.Id) })];
            }

            var dto = new UserProfileDto(
                user.Id,
                user.UserLogin,
                user.UserName,
                isOwner ? user.UserEmail : null,
                isOwner ? user.UserPhoneNumber : null,
                user.AvatarPath,
                isOwner ? user.UserRoles.Select(ur => ur.Role.Name).ToList() : [],
                _tracker.IsOnline(user.Id),
                user.CreatedAt,
                user.LastActivityAt,
                ads);

            return Ok(dto);
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

            return Ok(new AvatarUploadDto(user.AvatarPath!));
        }


        // --- Helpers ---

        private Task<bool> IsBlockedBidirectionalAsync(int userA, int userB) =>
            _db.UserBlocks.AnyAsync(b =>
                (b.SourceUserId == userA && b.TargetUserId == userB) ||
                (b.SourceUserId == userB && b.TargetUserId == userA));

        // Проверка: владелец ресурса или админ.
        private bool IsOwnerOrAdmin(int currentUserId, int resourceOwnerId) =>
            currentUserId == resourceOwnerId || User.HasRole("Admin") || User.HasRole("SuperAdmin");
    }

    // --- ClaimsPrincipal extensions ---
    public static class ClaimsPrincipalExtensions
    {
        public static bool TryGetUserId(this ClaimsPrincipal user, out int userId)
        {
            userId = 0;
            var value = user?.FindFirstValue("sub");
            return !string.IsNullOrWhiteSpace(value) && int.TryParse(value, out userId);
        }

        public static bool TryGetSessionId(this ClaimsPrincipal user, out Guid sessionId)
        {
            sessionId = Guid.Empty;
            var value = user?.FindFirstValue("sid");
            return value != null && Guid.TryParse(value, out sessionId);
        }

        public static bool HasRole(this ClaimsPrincipal user, string role) =>
            user.Claims.Where(c => c.Type == ClaimTypes.Role).Any(c => c.Value == role);

        public static bool IsPrivileged(this ClaimsPrincipal user) =>
            user.HasRole("Admin") || user.HasRole("SuperAdmin") || user.HasRole("Moderator");
    }
}
