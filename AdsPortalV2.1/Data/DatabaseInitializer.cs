using AdsPortalV2.Entities;
using AdsPortalV2.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;

namespace AdsPortalV2.Data;

using System.Linq;
using System.Collections.Generic;
using System;

public static class DatabaseInitializer
{
    public static void SeedDatabase(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // Database recreation is dangerous in non-development environments.
        // Control behavior with configuration: "Database:Recreate" = true to drop+migrate on startup.
        // Default: do not drop database.
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        try
        {
            var recreate = config.GetValue<bool>("Database:Recreate", false);
            if (recreate && env.IsDevelopment())
            {
                //context.Database.EnsureDeleted(); Зачем мы удаляем базу данных при каждом запуске в деве? Это жесть, можно случайно удалить важные данные. Лучше просто мигрировать, а если нужно чисто для тестов - юзать InMemory или SQLite в памяти.
            }

            context.Database.Migrate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Migration error: {ex}");
        }

        // Ensure any legacy string values are migrated from "CommentBan" to "ChatBan" in DB
        try
        {
            context.Database.ExecuteSqlRaw("UPDATE UserRestrictions SET Type = 'ChatBan' WHERE Type = 'CommentBan'");
        }
        catch { /* best-effort, ignore if table missing or SQL fails in dev */ }

        if (!context.Users.Any(u => u.UserLogin == "admin"))
        {
            var password = config["Admin:DefaultPassword"] ?? "admin123";

            var adminUser = new User
            {
                UserLogin = "admin",
                UserPasswordHash = PasswordService.Hash(password)
            };

            context.Users.Add(adminUser);
            context.SaveChanges();

            // Seed RBAC
            SeedRbac(context, adminUser.Id);
        }
        else if (!context.Roles.Any())
        {
            var adminUser = context.Users.First(u => u.UserLogin == "admin");
            SeedRbac(context, adminUser.Id);
        }

        if (!context.Categories.Any())
        {
            // Seed top-level categories only by name. Do not set ParentId here.
            context.Categories.AddRange(
                new Category { Name = "Электроника" },
                new Category { Name = "Бытовая техника" },
                new Category { Name = "Книги" },
                new Category { Name = "Одежда" }
            );
            context.SaveChanges();
        }

        // Rebuild category cache via ICategoryService (do not duplicate business logic here)
        try
        {
            scope.ServiceProvider.GetRequiredService<ICategoryService>().RebuildAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Category cache rebuild error: {ex}");
        }

        SeedLocations(context);
        // Seed additional demo users and ads (idempotent, non-destructive)
        SeedUsersAndAds(context);
    }
    private static void SeedUsersAndAds(AppDbContext context)
    {
        // Split into idempotent parts so seed can be re-run safely
        SeedUsers(context);
        SeedAds(context);
    }

    private static void SeedUsers(AppDbContext context)
    {
        // Ensure a base set of dev users exist. Update non-critical fields if missing.
        var desiredUsers = Enumerable.Range(1, 10).Select(i => new User
        {
            UserLogin = $"user{i}",
            UserPasswordHash = PasswordService.Hash($"password{i}"),
            UserName = $"Пользователь {i}",
            UserEmail = $"user{i}@example.com"
        }).ToArray();

        foreach (var du in desiredUsers)
        {
            var exist = context.Users.FirstOrDefault(u => u.UserLogin == du.UserLogin);
            if (exist == null)
            {
                context.Users.Add(du);
            }
            else
            {
                // Do not overwrite existing important fields. Fill missing metadata if any.
                if (string.IsNullOrWhiteSpace(exist.UserName)) exist.UserName = du.UserName;
                if (string.IsNullOrWhiteSpace(exist.UserEmail)) exist.UserEmail = du.UserEmail;
                if (string.IsNullOrWhiteSpace(exist.UserPasswordHash)) exist.UserPasswordHash = du.UserPasswordHash;
            }
        }

        context.SaveChanges();
    }

    private static void SeedAds(AppDbContext context)
    {
        // Do not be single-shot: create ads until we have a reasonable amount (100-300).
        var existingCount = context.Ads.Count();

        int toCreate = 0;

        var usersList = context.Users
            .Where(u => u.UserLogin.StartsWith("user"))
            .OrderBy(u => u.Id)
            .ToList();

        var categories = context.Categories.OrderBy(c => c.Id).ToList();
        var locations = context.Locations.Where(l => l.Type == LocationType.City).OrderBy(l => l.Id).ToList();

        if (!usersList.Any() || !categories.Any() || !locations.Any()) return;

        var titles = new[]
        {
            "iPhone 13 Pro 256GB отличное состояние",
            "Ноутбук Lenovo для работы и игр",
            "Стиральная машина LG почти новая",
            "Продам велосипед горный",
            "Куртка зимняя мужская",
            "Книга \"Чистый код\"",
            "Сдам квартиру на длительный срок",
            "Ремонт компьютеров и ноутбуков",
            "Куплю iPhone недорого",
            "Диван раскладной в хорошем состоянии"
        };

        var listingTypes = new[] { "sale", "rent", "service", "wanted" };
        var statuses = new[] { AdStatus.Active, AdStatus.PendingModeration, AdStatus.Rejected };
        var imageSeeds = new[]
        {
            "iphone", "laptop", "washing-machine", "bike",
            "sofa", "book", "jacket", "car", "watch", "table",
            "tv", "headphones", "camera", "shoes", "apartment"
        };

        var ads = new List<Ad>(toCreate);

        // Helper deterministic functions using FNV-1a (fast, deterministic across runtimes)
        static ulong FnV1aHash64(string input)
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;
            var hash = fnvOffset;
            var bytes = System.Text.Encoding.UTF8.GetBytes(input ?? "");
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= fnvPrime;
            }
            return hash;
        }

        static int DeterministicRange(int existingCount, int idx, int userId, int categoryId, string tag, int minInclusive, int maxExclusive)
        {
            var key = $"{existingCount}:{idx}:{userId}:{categoryId}:{tag}";
            var h = FnV1aHash64(key);
            var range = maxExclusive - minInclusive;
            if (range <= 0) return minInclusive;
            return (int)(h % (ulong)range) + minInclusive;
        }

        static double DeterministicDouble(int existingCount, int idx, int userId, int categoryId, string tag)
        {
            var key = $"{existingCount}:{idx}:{userId}:{categoryId}:{tag}";
            var h = FnV1aHash64(key);
            return (h % ulong.MaxValue) / (double)ulong.MaxValue;
        }

        // stable target: use fixed target to avoid random shrink/expand between runs
        const int targetTotal = 200;
        if (existingCount >= targetTotal) return;

        // rebuild toCreate based on stable target
        toCreate = targetTotal - existingCount;

        // prepare new keys to avoid duplicates within this run
        var newKeys = new HashSet<string>();
        // load existing keys once (Title + UserId) to avoid per-iteration DB queries
        var existingKeys = context.Ads
            .Select(a => (a.Title ?? "") + "_" + a.UserId)
            .ToHashSet();
        // base date for deterministic createdAt
        var baseDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var titleByCategory = new Dictionary<string, string[]>
        {
            ["Электроника"] = new[] { "iPhone 13 Pro 256GB отличное состояние", "Ноутбук Lenovo для работы и игр", "Наушники Sony WH-1000XM4" },
            ["Бытовая техника"] = new[] { "Стиральная машина LG почти новая", "Холодильник Bosch" },
            ["Книги"] = new[] { "Книга \"Чистый код\"", "CLR via C#" },
            ["Одежда"] = new[] { "Куртка зимняя мужская", "Кроссовки Adidas" }
        };

        int created = 0;
        int idx = 0;
        var maxAttempts = Math.Max(toCreate * 5, toCreate + 50);
        while (created < toCreate && idx < maxAttempts)
        {
            // distribute deterministically to guarantee coverage
            var user = usersList[idx % usersList.Count];
            var category = categories[idx % categories.Count];
            var location = locations[idx % locations.Count];

            var listingType = listingTypes[DeterministicRange(existingCount, idx, user.Id, category.Id, "listing", 0, listingTypes.Length)];

            var rawPrice = listingType switch
            {
                "sale" => DeterministicRange(existingCount, idx, user.Id, category.Id, "price_sale", 50, 2000),
                "rent" => DeterministicRange(existingCount, idx, user.Id, category.Id, "price_rent", 5, 200),
                "service" => DeterministicRange(existingCount, idx, user.Id, category.Id, "price_service", 10, 500),
                "wanted" => DeterministicRange(existingCount, idx, user.Id, category.Id, "price_wanted", 1, 1000),
                _ => DeterministicRange(existingCount, idx, user.Id, category.Id, "price_default", 10, 1000)
            };

            var createdAt = baseDate.AddDays(-DeterministicRange(existingCount, idx, user.Id, category.Id, "days", 0, 60));

            // deterministic image seed
            var seed = imageSeeds[(existingCount + idx + category.Id) % imageSeeds.Length];

            // choose title in relation to category for realism
            string title;
            if (titleByCategory.TryGetValue(category.Name, out var pool) && pool.Length > 0)
                title = pool[(existingCount + idx) % pool.Length];
            else
                title = titles[(existingCount + idx) % titles.Length];

            var description = ((existingCount + idx) % 7 == 0) ? null : $"Описание объявления #{existingCount + created + 1}. Хорошее состояние, без проблем. Звоните.";
            decimal? adPrice = ((existingCount + idx) % 11 == 0) ? 0m : Math.Round((decimal)rawPrice, 2);

            // business-driven status selection (deterministic)
            AdStatus status;
            if (description == null)
            {
                status = AdStatus.PendingModeration;
            }
            else if (adPrice == 0m)
            {
                status = DeterministicDouble(existingCount, idx, user.Id, category.Id, "status") < 0.5 ? AdStatus.Rejected : AdStatus.PendingModeration;
            }
            else
            {
                var sd = DeterministicDouble(existingCount, idx, user.Id, category.Id, "status");
                status = sd < 0.75 ? AdStatus.Active : (sd < 0.9 ? AdStatus.PendingModeration : AdStatus.Rejected);
            }

            var key = (title ?? "") + "_" + user.Id;
            // check existence in DB (avoid loading full table) and skip duplicates within this run
            if (context.Ads.Any(a => a.Title == title && a.UserId == user.Id) || newKeys.Contains(key))
            {
                idx++;
                continue; // skip duplicates
            }

            var img = new AdImage
            {
                FilePath = $"https://picsum.photos/seed/{seed}_{listingType}_{category.Id}_{existingCount + created + 1}/900/700",
                SortOrder = 0
            };

            var ad = new Ad
            {
                UserId = user.Id,
                CategoryId = category.Id,
                LocationId = location.Id,
                Title = title,
                Description = description,
                Price = adPrice,
                ListingType = listingType,
                IsNegotiable = DeterministicRange(existingCount, idx, user.Id, category.Id, "neg", 0, 2) == 1,
                CreatedAt = createdAt,
                UpdatedAt = createdAt.AddHours(DeterministicRange(existingCount, idx, user.Id, category.Id, "hours", 0, 48)),
                Status = status,
                ViewsCount = DeterministicRange(existingCount, idx, user.Id, category.Id, "views", 0, 1000),
                FavoritesCount = DeterministicRange(existingCount, idx, user.Id, category.Id, "favs", 0, 200),
                Images = new List<AdImage> { img },
                MainImage = img
            };

            ads.Add(ad);
            newKeys.Add(key);
            created++;
            idx++;
        }

        if (ads.Count > 0)
        {
            context.Ads.AddRange(ads);
            context.SaveChanges();
        }
    }

    private static void SeedRbac(AppDbContext context, int adminUserId)
    {
        if (context.Roles.Any()) return;

        Role[] roles = [
            new Role { Name = "User" },
            new Role { Name = "Moderator" },
            new Role { Name = "Admin" },
            new Role { Name = "SuperAdmin" }
        ];
        context.Roles.AddRange(roles);
        context.SaveChanges();

        string[] permNames = [
            "ads.view", "ads.view_hidden", "ads.create", "ads.edit", "ads.delete", "ads.moderate",
            "users.ban", "users.unban", "users.edit",
            "roles.assign", "roles.revoke",
            "logs.view"
        ];
        var perms = permNames.Select(n => new Permission { Name = n }).ToArray();
        context.Permissions.AddRange(perms);
        context.SaveChanges();

        var permMap = perms.ToDictionary(p => p.Name, p => p.Id);
        var roleMap = roles.ToDictionary(r => r.Name, r => r.Id);

        void Assign(string role, params string[] pms)
        {
            foreach (var p in pms)
                context.RolePermissions.Add(new RolePermission { RoleId = roleMap[role], PermissionId = permMap[p] });
        }

        Assign("User", "ads.view", "ads.create", "ads.edit", "ads.delete");
        Assign("Moderator", "ads.view", "ads.view_hidden", "ads.moderate", "users.ban");
        Assign("Admin", "ads.view", "ads.view_hidden", "users.unban", "users.edit", "logs.view");
        Assign("SuperAdmin", "ads.view", "ads.view_hidden", "ads.create", "ads.edit", "ads.delete",
            "ads.moderate", "users.ban", "users.unban", "users.edit", "roles.assign", "roles.revoke", "logs.view");

        context.SaveChanges();

        context.UserRoles.Add(new UserRole { UserId = adminUserId, RoleId = roleMap["SuperAdmin"] });
        context.SaveChanges();
    }

    private static void SeedLocations(AppDbContext context)
    {
        if (context.Locations.Any()) return;

        var locations = new List<Location>();

        foreach (var regionName in BelarusGeoSeed.Regions)
            locations.Add(new Location { Name = regionName, Type = LocationType.Region });

        context.Locations.AddRange(locations);
        context.SaveChanges();

        var regionIds = context.Locations
            .Where(x => x.Type == LocationType.Region)
            .ToDictionary(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase);

        var cityLocations = new List<Location>();
        foreach (var region in BelarusGeoSeed.Cities)
        {
            if (!regionIds.TryGetValue(region.Region, out var regionId)) continue;

            foreach (var cityName in region.Cities)
                cityLocations.Add(new Location { Name = cityName, Type = LocationType.City, ParentId = regionId });
        }

        context.Locations.AddRange(cityLocations);
        context.SaveChanges();

        var districtLocations = new List<Location>();
        foreach (var city in BelarusGeoSeed.Districts)
        {
            var cityEntry = context.Locations.FirstOrDefault(x => x.Type == LocationType.City && x.Name == city.City);
            if (cityEntry == null) continue;

            foreach (var districtName in city.Districts)
                districtLocations.Add(new Location { Name = districtName, Type = LocationType.District, ParentId = cityEntry.Id });
        }

        if (districtLocations.Count > 0)
        {
            context.Locations.AddRange(districtLocations);
            context.SaveChanges();
        }
    }

    // RebuildCategoryHierarchy intentionally removed from DatabaseInitializer.
    // Category graph/path/building logic lives in ICategoryService.RebuildAsync().
}