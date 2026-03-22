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
                .FirstOrDefaultAsync(u => u.Id == id);

            return user is null ? NotFound() : Ok(user);
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
        private async Task<bool> IsOwnerOrAdminAsync(int currentUserId, int resourceOwnerId)
        {
            if (currentUserId == resourceOwnerId) return true;

            // Сначала проверяем роль в токене
            if (User.IsInRole("Admin")) return true;

            // Если роли нет в токене, падаем на флаг в БД
            var currentUser = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == currentUserId)
                .Select(u => new { u.IsAdmin })
                .FirstOrDefaultAsync();

            return currentUser?.IsAdmin ?? false;
        }
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
    }
}
