using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Services;
using Microsoft.EntityFrameworkCore;

public static class DatabaseInitializer
{
    public static void SeedDatabase(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        try
        {
            if (context.Database.GetPendingMigrations().Any())
                context.Database.Migrate();
        }
        catch
        {
            // Игнорируем ошибки миграции при несовпадении модели в рантайме (dev).
        }

        if (!context.Users.Any(u => u.UserLogin == "admin"))
        {
            var password = config["Admin:DefaultPassword"] ?? "admin123";

            context.Users.Add(new User
            {
                UserLogin = "admin",
                UserPasswordHash = PasswordService.Hash(password),
                IsAdmin = true
            });
            context.SaveChanges();
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

    private static void SeedLocations(AppDbContext context)
    {
        static string Key(int id, string name) => $"{id}:{name}".ToLowerInvariant();

        var regions = context.Regions.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var missingRegions = BelarusGeoSeed.Regions
            .Where(name => !regions.ContainsKey(name))
            .Select(name => new Region { Name = name })
            .ToList();

        if (missingRegions.Count > 0)
        {
            context.Regions.AddRange(missingRegions);
            context.SaveChanges();
            regions = context.Regions.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        }

        var cities = context.Cities
            .Select(x => new { x.Id, x.Name, x.RegionId })
            .ToList()
            .ToDictionary(x => Key(x.RegionId, x.Name), x => x.Id);

        var missingCities = BelarusGeoSeed.Cities
            .SelectMany(x => x.Cities.Select(name => new City { Name = name, RegionId = regions[x.Region].Id }))
            .Where(x => !cities.ContainsKey(Key(x.RegionId, x.Name)))
            .ToList();

        if (missingCities.Count > 0)
        {
            context.Cities.AddRange(missingCities);
            context.SaveChanges();
        }

        var cityMap = context.Cities.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var districts = context.Districts
            .Select(x => new { x.Name, x.CityId })
            .ToList()
            .ToDictionary(x => Key(x.CityId, x.Name), x => x.CityId);

        var missingDistricts = BelarusGeoSeed.Districts
            .Where(x => cityMap.ContainsKey(x.City))
            .SelectMany(x => x.Districts.Select(name => new District { Name = name, CityId = cityMap[x.City].Id }))
            .Where(x => !districts.ContainsKey(Key(x.CityId, x.Name)))
            .ToList();

        if (missingDistricts.Count > 0)
        {
            context.Districts.AddRange(missingDistricts);
            context.SaveChanges();
        }
    }
}