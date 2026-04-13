using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
        public async Task<ActionResult<UserProfileResponseDto>> GetById(int id)
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
                FilePathHelpers.EnsurePublicPath(_db.AdImages.Where(img => img.Id == ad.MainImageId).Select(img => img.FilePath).FirstOrDefault())))
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
                    FilePathHelpers.EnsurePublicPath(user.AvatarPath),
                    [.. user.UserRoles.Select(ur => ur.Role?.Name).OfType<string>()],
                    _tracker.IsOnline(user.Id.ToString()),
                    user.CreatedAt,
                    user.LastActivityAt,
                    ads),
                currentUserFavorites);

            return Ok(dto);
        }

        [Authorize]
        [HttpPatch("{id:int}")]
        public async Task<ActionResult<PatchResultDto>> Patch(int id, [FromBody] JsonElement body)
        {
            if (!User.TryGetUserId(out var currentUserId))
                return Unauthorized();

            if (!IsOwnerOrAdmin(currentUserId, id))
                return Forbid();

            var user = await _db.Users.FindAsync(id);
            if (user is null) return NotFound();

            var updated = new HashSet<string>();
            var skipped = new List<PatchIssueDto>();
            var errors = new List<PatchIssueDto>();

            if (body.TryGetProperty("password", out var passwordProp))
            {
                var password = passwordProp.ValueKind == JsonValueKind.Null ? null : passwordProp.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(password))
                    errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, UserFieldNames.Password, "Password cannot be empty."));
                else
                {
                    user.UserPasswordHash = PasswordService.Hash(password);
                    updated.Add(UserFieldNames.Password);
                }
            }

            if (body.TryGetProperty("userLogin", out var userLoginElem))
                PatchHelpers.UpdateString(userLoginElem, user.UserLogin, v => user.UserLogin = v, UserFieldNames.UserLogin, updated, skipped, errors);

            if (body.TryGetProperty("userName", out var userNameElem))
                PatchHelpers.UpdateNullableString(userNameElem, user.UserName, v => user.UserName = v, UserFieldNames.UserName, updated, skipped, errors);

            if (body.TryGetProperty("userEmail", out var userEmailElem))
                PatchHelpers.UpdateNullableString(userEmailElem, user.UserEmail, v => user.UserEmail = v, UserFieldNames.UserEmail, updated, skipped, errors);

            if (body.TryGetProperty("userPhoneNumber", out var userPhoneElem))
                PatchHelpers.UpdateNullableString(userPhoneElem, user.UserPhoneNumber, v => user.UserPhoneNumber = v, UserFieldNames.UserPhoneNumber, updated, skipped, errors);

            if (body.TryGetProperty("avatarPath", out var avatarElem))
                PatchHelpers.UpdateNullableString(avatarElem, user.AvatarPath, v => user.AvatarPath = v, UserFieldNames.AvatarPath, updated, skipped, errors);

            // Early return on validation errors — do not persist invalid state
            if (errors.Count > 0)
                return BadRequest(new PatchResultDto(false, updated, skipped, errors));

            await _db.SaveChangesAsync();

            return Ok(new PatchResultDto(true, updated, skipped, errors));
        }

        [Authorize]
        [HttpGet("{id:int}/favorites")]
        public async Task<ActionResult<IReadOnlyCollection<FavoriteAdDto>>> GetFavorites(int id)
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
                    FilePathHelpers.EnsurePublicPath(_db.AdImages.Where(img => img.Id == x.Ad.MainImageId).Select(img => img.FilePath).FirstOrDefault()),
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
        [HttpPost("{targetId:int}/blocks")]
        public async Task<IActionResult> BlockUser(int targetId, CancellationToken ct)
        {
            if (!User.TryGetUserId(out var userId))
                return Unauthorized();

            if (userId == targetId)
                return BadRequest(new ApiError("self_block", "Cannot block yourself"));

            var exists = await _db.Users
                .AnyAsync(u => u.Id == targetId, ct);

            if (!exists)
                return NotFound(new ApiError("not_found", "User not found"));

            var entity = new UserBlock
            {
                SourceUserId = userId,
                TargetUserId = targetId,
                CreatedAt = DateTime.UtcNow
            };

            _db.UserBlocks.Add(entity);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // UNIQUE(SourceUserId, TargetUserId)
                HttpContext.RequestServices.GetRequiredService<IMemoryCache>().Remove($"block:{Math.Min(userId,targetId)}:{Math.Max(userId,targetId)}");
                return Ok(new BlockDto
                {
                    TargetUserId = targetId,
                    CreatedAt = entity.CreatedAt
                });
            }

            // invalidate cache
            HttpContext.RequestServices.GetRequiredService<IMemoryCache>().Remove($"block:{Math.Min(userId,targetId)}:{Math.Max(userId,targetId)}");

            return CreatedAtAction(nameof(GetMyBlocks), new { }, new BlockDto
            {
                TargetUserId = targetId,
                CreatedAt = entity.CreatedAt
            });
        }

        [Authorize]
        [HttpDelete("{targetId:int}/blocks")]
        public async Task<IActionResult> UnblockUser(int targetId, CancellationToken ct)
        {
            if (!User.TryGetUserId(out var userId))
                return Unauthorized();

            var block = await _db.UserBlocks
                .FirstOrDefaultAsync(b =>
                    b.SourceUserId == userId &&
                    b.TargetUserId == targetId, ct);

            if (block == null)
                return NoContent(); // idempotent

            _db.UserBlocks.Remove(block);
            await _db.SaveChangesAsync(ct);

            // invalidate cache
            HttpContext.RequestServices.GetRequiredService<IMemoryCache>().Remove($"block:{Math.Min(userId,targetId)}:{Math.Max(userId,targetId)}");

            return NoContent();
        }

        [Authorize]
        [HttpGet("blocks")]
        public async Task<ActionResult<List<BlockListItemDto>>> GetMyBlocks(CancellationToken ct)
        {
            if (!User.TryGetUserId(out var userId))
                return Unauthorized();

            var blocks = await _db.UserBlocks
                .Where(b => b.SourceUserId == userId)
                .OrderByDescending(b => b.CreatedAt)
                .Join(
                    _db.Users,
                    b => b.TargetUserId,
                    u => u.Id,
                    (b, u) => new BlockListItemDto
                    {
                        TargetUserId = b.TargetUserId,
                        CreatedAt = b.CreatedAt,
                        User = new BlockUserDto
                        {
                            Id = u.Id,
                            // Prefer visible display name, fall back to login when name is empty
                            Username = u.UserName ?? u.UserLogin ?? string.Empty,
                            // Return null when no avatar set to avoid empty-string payloads
                            AvatarUrl = string.IsNullOrWhiteSpace(u.AvatarPath) ? null : FilePathHelpers.EnsurePublicPath(u.AvatarPath)
                        }
                    })
                .ToListAsync(ct);

            return Ok(blocks);
        }

        [Authorize]
        [HttpPost("{id:int}/favorites")]
        public async Task<ActionResult<FavoriteMutationDto>> AddToFavorites(int id, [FromBody] int adId)
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
        public async Task<ActionResult> RemoveFromFavorites(int id, int adId)
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
        public async Task<ActionResult<IReadOnlyCollection<UserAdDto>>> GetAds(int id)
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
                FilePathHelpers.EnsurePublicPath(_db.AdImages.Where(img => img.Id == ad.MainImageId).Select(img => img.FilePath).FirstOrDefault())))
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

        [Authorize]
        [HttpPost("{id:int}/upload-avatar")]
        public async Task<ActionResult<AvatarUploadDto>> UploadAvatar(int id, IFormFile avatar)
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
                    user.AvatarPath = FilePathHelpers.EnsurePublicPath($"files/{id}/Avatars/{avatarFileName}?v={version}");
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
