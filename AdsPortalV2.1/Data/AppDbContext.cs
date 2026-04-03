using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<UserReview> UserReviews => Set<UserReview>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Ad> Ads => Set<Ad>();
    public DbSet<AdImage> AdImages => Set<AdImage>();
    public DbSet<AdminLog> AdminLogs => Set<AdminLog>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<UserFavoriteAd> UserFavoriteAds => Set<UserFavoriteAd>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<District> Districts => Set<District>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UserFavoriteAd>()
            .HasOne(fav => fav.User)
            .WithMany(user => user.Favorites)
            .HasForeignKey(fav => fav.UserId)
            .OnDelete(DeleteBehavior.NoAction); // Изменено на NoAction

        modelBuilder.Entity<UserFavoriteAd>()
            .HasOne(fav => fav.Ad)
            .WithMany()
            .HasForeignKey(fav => fav.AdId)
            .OnDelete(DeleteBehavior.NoAction); // Изменено на NoAction

        modelBuilder.Entity<Ad>()
            .Property(a => a.Title)
            .IsRequired();

        modelBuilder.Entity<Ad>()
            .HasOne(a => a.Category)
            .WithMany(c => c.Ads)
            .HasForeignKey(a => a.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Ad>()
            .Property(a => a.Price)
            .HasPrecision(10, 2);

        modelBuilder.Entity<Ad>()
            .Property(a => a.ModerationStatus)
            .HasDefaultValue(ModerationStatus.Pending);

        // Seed initial categories
        SeedCategories.Seed(modelBuilder);

        // Conversations
        modelBuilder.Entity<Conversation>()
            .HasOne(c => c.Seller)
            .WithMany(u => u.ConversationsAsSeller)
            .HasForeignKey(c => c.SellerId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Conversation>()
            .HasOne(c => c.Buyer)
            .WithMany(u => u.ConversationsAsBuyer)
            .HasForeignKey(c => c.BuyerId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Conversation>()
            .HasOne(c => c.Ad)
            .WithMany(a => a.Conversations)
            .HasForeignKey(c => c.AdId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Conversation>()
            .HasIndex(c => new { c.SellerId, c.BuyerId, c.AdId })
            .IsUnique();

        modelBuilder.Entity<Conversation>()
            .HasIndex(c => c.LastMessageTimestamp);

        modelBuilder.Entity<Ad>()
            .HasOne(a => a.User)
            .WithMany(u => u.Ads)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.NoAction); // Удаление пользователя больше не затрагивает объявления

        modelBuilder.Entity<Notification>()
            .HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Notification>()
            .HasOne(n => n.Ad)
            .WithMany()
            .HasForeignKey(n => n.AdId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<UserReview>()
            .HasOne(r => r.Reviewer)
            .WithMany(u => u.ReviewsWritten)
            .HasForeignKey(r => r.ReviewerId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<UserReview>()
            .HasOne(r => r.TargetUser)
            .WithMany(u => u.ReviewsReceived)
            .HasForeignKey(r => r.TargetUserId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<City>()
            .HasOne(c => c.Region)
            .WithMany(r => r.Cities)
            .HasForeignKey(c => c.RegionId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<District>()
            .HasOne(d => d.City)
            .WithMany(c => c.Districts)
            .HasForeignKey(d => d.CityId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Ad>()
            .HasOne(a => a.CityRef)
            .WithMany()
            .HasForeignKey(a => a.CityId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Ad>()
            .HasOne(a => a.District)
            .WithMany()
            .HasForeignKey(a => a.DistrictId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
