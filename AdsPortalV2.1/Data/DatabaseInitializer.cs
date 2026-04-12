using AdsPortalV2.Entities;
using AdsPortalV2.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;

namespace AdsPortalV2.Data;

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
            context.Categories.AddRange(
                new Category { Name = "Электроника", ParentId = null },
                new Category { Name = "Бытовая техника", ParentId = null },
                new Category { Name = "Книги", ParentId = null },
                new Category { Name = "Одежда", ParentId = null }
            );
            context.SaveChanges();
        }

        SeedLocations(context);
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
}