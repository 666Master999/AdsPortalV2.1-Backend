using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Data;

public static class SeedCategories
{
    public static void Seed(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>().HasData(
            new Category { Id = 1, Name = "Электроника", ParentId = null },
            new Category { Id = 2, Name = "Бытовая техника", ParentId = null },
            new Category { Id = 3, Name = "Книги", ParentId = null },
            new Category { Id = 4, Name = "Одежда", ParentId = null }
        );
    }
}