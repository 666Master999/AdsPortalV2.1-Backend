using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> AuthSessions => Set<UserSession>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRestriction> UserRestrictions => Set<UserRestriction>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<UserReview> UserReviews => Set<UserReview>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Ad> Ads => Set<Ad>();
    public DbSet<AdImage> AdImages => Set<AdImage>();
    public DbSet<FtsResult> FtsResults => Set<FtsResult>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<UserFavoriteAd> UserFavoriteAds => Set<UserFavoriteAd>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Location> Locations => Set<Location>();

    public override int SaveChanges()
    {
        ValidateLocations();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ValidateLocations();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void ValidateLocations()
    {
        foreach (var entry in ChangeTracker.Entries<Location>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var type = entry.Entity.Type;
            var parentId = entry.Entity.ParentId;

            if (type == LocationType.Region && parentId != null)
                throw new InvalidOperationException("Регион не может иметь родителя");

            if (type == LocationType.City && parentId == null)
                throw new InvalidOperationException("Город может находиться только внутри региона");

            if (type == LocationType.District && parentId == null)
                throw new InvalidOperationException("Район может находиться только внутри города");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Auth Sessions ──
        modelBuilder.Entity<UserSession>().ToTable("AuthSessions");
        modelBuilder.Entity<UserSession>().Property(s => s.Id).ValueGeneratedNever();
        modelBuilder.Entity<UserSession>().HasOne(s => s.User).WithMany(u => u.Sessions).HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserSession>().HasIndex(s => s.RefreshTokenHash).IsUnique();
        modelBuilder.Entity<UserSession>().HasIndex(s => s.UserId);

        // ── RBAC ──
        modelBuilder.Entity<UserRole>().HasKey(ur => new { ur.UserId, ur.RoleId });
        modelBuilder.Entity<UserRole>().HasOne(ur => ur.User).WithMany(u => u.UserRoles).HasForeignKey(ur => ur.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserRole>().HasOne(ur => ur.Role).WithMany(r => r.UserRoles).HasForeignKey(ur => ur.RoleId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RolePermission>().HasKey(rp => new { rp.RoleId, rp.PermissionId });
        modelBuilder.Entity<RolePermission>().HasOne(rp => rp.Role).WithMany(r => r.RolePermissions).HasForeignKey(rp => rp.RoleId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RolePermission>().HasOne(rp => rp.Permission).WithMany(p => p.RolePermissions).HasForeignKey(rp => rp.PermissionId).OnDelete(DeleteBehavior.Cascade);

        // ── Restrictions ──
        modelBuilder.Entity<UserRestriction>().HasOne(r => r.User).WithMany(u => u.Restrictions).HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserRestriction>().Property(r => r.Type).HasConversion<string>().HasMaxLength(30);
        modelBuilder.Entity<UserRestriction>().HasIndex(r => new { r.UserId, r.Type });

        // ── UserBlocks (user-to-user) ──
        modelBuilder.Entity<UserBlock>().HasOne(b => b.SourceUser).WithMany(u => u.BlocksGiven).HasForeignKey(b => b.SourceUserId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<UserBlock>().HasOne(b => b.TargetUser).WithMany(u => u.BlocksReceived).HasForeignKey(b => b.TargetUserId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<UserBlock>().HasIndex(b => new { b.SourceUserId, b.TargetUserId }).IsUnique();

        // ── Favorites ──
        modelBuilder.Entity<UserFavoriteAd>().HasOne(fav => fav.User).WithMany(user => user.Favorites).HasForeignKey(fav => fav.UserId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<UserFavoriteAd>().HasOne(fav => fav.Ad).WithMany().HasForeignKey(fav => fav.AdId).OnDelete(DeleteBehavior.NoAction);

        // ── Ads ──
        modelBuilder.Entity<Ad>().Property(a => a.Title).IsRequired();
        modelBuilder.Entity<Ad>().Property(a => a.Status).HasConversion<string>().HasMaxLength(30);
        modelBuilder.Entity<Ad>().HasOne(a => a.Location).WithMany().HasForeignKey(a => a.LocationId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Ad>().HasOne(a => a.Category).WithMany(c => c.Ads).HasForeignKey(a => a.CategoryId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Ad>().HasOne(a => a.User).WithMany(u => u.Ads).HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Ad>().HasOne(a => a.MainImage).WithMany().HasForeignKey(a => a.MainImageId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Ad>().Property(a => a.Price).HasPrecision(10, 2);
        modelBuilder.Entity<Ad>().HasIndex(a => a.CreatedAt);
        modelBuilder.Entity<Ad>().HasIndex(a => a.MainImageId);
        modelBuilder.Entity<Ad>().HasIndex(a => new { a.LocationId, a.CreatedAt });
        modelBuilder.Entity<Ad>().HasIndex(a => a.Price);
        modelBuilder.Entity<Ad>().HasIndex(a => a.CategoryId);
        modelBuilder.Entity<Ad>().HasIndex(a => new { a.Status, a.UserId });

        modelBuilder.Entity<FtsResult>().HasNoKey();

        modelBuilder.Entity<AdImage>().HasOne(i => i.Ad).WithMany(a => a.Images).HasForeignKey(i => i.AdId).OnDelete(DeleteBehavior.Cascade);

        // SeedCategories удалён, сидирование категорий теперь только через DatabaseInitializer

        // ── Conversations ──
        modelBuilder.Entity<Conversation>().HasOne(c => c.Seller).WithMany(u => u.ConversationsAsSeller).HasForeignKey(c => c.SellerId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Conversation>().HasOne(c => c.Buyer).WithMany(u => u.ConversationsAsBuyer).HasForeignKey(c => c.BuyerId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Conversation>().HasOne(c => c.Ad).WithMany(a => a.Conversations).HasForeignKey(c => c.AdId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Conversation>().HasIndex(c => new { c.SellerId, c.BuyerId, c.AdId }).IsUnique();
        modelBuilder.Entity<Conversation>().HasIndex(c => c.LastMessageTimestamp);

        // ── Notifications ──
        modelBuilder.Entity<Notification>().HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Notification>().HasOne(n => n.Ad).WithMany().HasForeignKey(n => n.AdId).OnDelete(DeleteBehavior.SetNull);

        // ── Reviews ──
        modelBuilder.Entity<UserReview>().HasOne(r => r.Reviewer).WithMany(u => u.ReviewsWritten).HasForeignKey(r => r.ReviewerId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<UserReview>().HasOne(r => r.TargetUser).WithMany(u => u.ReviewsReceived).HasForeignKey(r => r.TargetUserId).OnDelete(DeleteBehavior.NoAction);

        // ── Locations ──
        modelBuilder.Entity<Location>().HasOne(l => l.Parent).WithMany(l => l.Children).HasForeignKey(l => l.ParentId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Location>().HasIndex(l => l.ParentId);
        modelBuilder.Entity<Location>().HasIndex(l => new { l.ParentId, l.Name }).IsUnique();

        // ── AuditLog ──
        modelBuilder.Entity<AuditLog>().HasOne(a => a.ActorUser).WithMany(u => u.AuditLogs).HasForeignKey(a => a.ActorUserId).OnDelete(DeleteBehavior.NoAction);
    }
}
