using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Services;
using Microsoft.Extensions.DependencyInjection;

public static class DatabaseInitializer
{
    public static void SeedDatabase(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Ensure database is created
        context.Database.EnsureCreated();

        // Add default admin user if none exist
        if (!context.Users.Any(u => u.UserLogin == "admin"))
        {
            var hashedPassword = PasswordService.Hash("admin123");

            context.Users.Add(new User
            {
                UserLogin = "admin",
                UserPasswordHash = hashedPassword,
                IsAdmin = true
            });
            context.SaveChanges();
        }

        // Add default categories if none exist
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
    }
}