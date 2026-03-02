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
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<ChatThread> ChatThreads => Set<ChatThread>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<Complaint> Complaints => Set<Complaint>();
    public DbSet<AdminLog> AdminLogs => Set<AdminLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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

        modelBuilder.Entity<Category>()
            .HasOne(c => c.Parent)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ChatThread>()
            .HasOne(t => t.Buyer)
            .WithMany()
            .HasForeignKey(t => t.BuyerId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<ChatThread>()
            .HasOne(t => t.Ad)
            .WithMany(a => a.ChatThreads)
            .HasForeignKey(t => t.AdId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<ChatMessage>()
            .HasOne(m => m.Sender)
            .WithMany(u => u.ChatMessages)
            .HasForeignKey(m => m.SenderId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<ChatMessage>()
            .HasOne(m => m.ChatThread)
            .WithMany(t => t.Messages)
            .HasForeignKey(m => m.ChatThreadId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Complaint>()
            .HasOne(c => c.User)
            .WithMany(u => u.Complaints)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Complaint>()
            .HasOne(c => c.Ad)
            .WithMany(a => a.Complaints)
            .HasForeignKey(c => c.AdId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Favorite>()
            .HasOne(f => f.User)
            .WithMany(u => u.Favorites)
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Favorite>()
            .HasOne(f => f.Ad)
            .WithMany(a => a.Favorites)
            .HasForeignKey(f => f.AdId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Ad>()
            .Property(a => a.Price)
            .HasColumnType("decimal(18,2)");
    }
}
