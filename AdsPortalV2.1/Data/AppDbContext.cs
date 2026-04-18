using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Text.Json;

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
    public DbSet<CategoryAttribute> CategoryAttributes => Set<CategoryAttribute>();
    public DbSet<CategoryAttributeOption> CategoryAttributeOptions => Set<CategoryAttributeOption>();
    public DbSet<AdAttributeValue> AdAttributeValues => Set<AdAttributeValue>();
    public DbSet<Ad> Ads => Set<Ad>();
    public DbSet<AdImage> AdImages => Set<AdImage>();
    public DbSet<FtsResult> FtsResults => Set<FtsResult>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<UserFavoriteAd> UserFavoriteAds => Set<UserFavoriteAd>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public override int SaveChanges()
    {
        ValidateLocations();
        var result = base.SaveChanges();
        return result;
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ValidateLocations();
        var result = await base.SaveChangesAsync(cancellationToken);
        return result;
    }

    // Materialize domain events from tracked entities into OutboxMessages.
    // This method is intended to be called explicitly by application services
    // after EF has assigned identities (SaveChanges) so event payloads may
    // include DB-generated ids.
    public void MaterializeDomainEvents()
    {
        var entityEvents = ChangeTracker.Entries<Entity>()
            .SelectMany(entry => entry.Entity.DomainEvents.Select(ev => (ev, entry.Entity)))
            .ToArray();

        if (entityEvents.Length == 0)
            return;

        foreach (var (domainEvent, owner) in entityEvents)
        {
            if (domainEvent.EventId == Guid.Empty)
            {
                try { domainEvent.EventId = Guid.NewGuid(); } catch { }
            }

            OutboxMessages.Add(new OutboxMessage
            {
                EventId = domainEvent.EventId,
                EventType = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
                PayloadJson = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
                CreatedAt = domainEvent.OccurredAt,
                AvailableAt = domainEvent.OccurredAt,
                Status = OutboxStatus.Pending,
                AttemptCount = 0
            });
        }
    }

    public void ClearDomainEvents()
    {
        // clear entity-raised events after they have been materialized
        foreach (var entity in ChangeTracker.Entries<Entity>())
            entity.Entity.DomainEvents.Clear();

        // also clear any in-memory buffer if present (best-effort)
        try
        {
            var buffer = this.GetService<IDomainEventBuffer>();
            buffer?.Clear();
        }
        catch { }
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

        // ── Categories ──
        modelBuilder.Entity<Category>().Property(c => c.Name).IsRequired().HasMaxLength(200);
        modelBuilder.Entity<Category>().Property(c => c.Path).HasMaxLength(1000);
        modelBuilder.Entity<Category>().HasOne(c => c.Parent).WithMany(c => c.Children).HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Category>().HasIndex(c => c.ParentId);
        modelBuilder.Entity<Category>().HasIndex(c => new { c.ParentId, c.Name }).IsUnique();
        modelBuilder.Entity<Category>().HasIndex(c => c.Path);

        modelBuilder.Entity<CategoryAttribute>().Property(a => a.Name).IsRequired().HasMaxLength(200);
        modelBuilder.Entity<CategoryAttribute>().Property(a => a.Slug).IsRequired().HasMaxLength(100);
        modelBuilder.Entity<CategoryAttribute>().HasOne(a => a.Category).WithMany(c => c.Attributes).HasForeignKey(a => a.CategoryId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CategoryAttribute>().HasIndex(a => new { a.CategoryId, a.Slug }).IsUnique();

        modelBuilder.Entity<CategoryAttributeOption>().Property(o => o.Value).IsRequired().HasMaxLength(200);
        modelBuilder.Entity<CategoryAttributeOption>().HasOne(o => o.CategoryAttribute).WithMany(a => a.Options).HasForeignKey(o => o.CategoryAttributeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CategoryAttributeOption>().HasIndex(o => new { o.CategoryAttributeId, o.Value }).IsUnique();

        modelBuilder.Entity<AdAttributeValue>().Property(v => v.Value).IsRequired().HasMaxLength(2000);
        modelBuilder.Entity<AdAttributeValue>().HasOne(v => v.Ad).WithMany(a => a.AttributeValues).HasForeignKey(v => v.AdId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AdAttributeValue>().HasOne(v => v.Attribute).WithMany(a => a.AdValues).HasForeignKey(v => v.AttributeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AdAttributeValue>().HasIndex(v => new { v.AdId, v.AttributeId }).IsUnique();
        modelBuilder.Entity<AdAttributeValue>().HasIndex(v => new { v.AttributeId, v.Value, v.AdId });

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

        // ── Conversations ──
        modelBuilder.Entity<Conversation>().HasOne(c => c.Seller).WithMany(u => u.ConversationsAsSeller).HasForeignKey(c => c.SellerId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Conversation>().HasOne(c => c.Buyer).WithMany(u => u.ConversationsAsBuyer).HasForeignKey(c => c.BuyerId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Conversation>().HasOne(c => c.Ad).WithMany(a => a.Conversations).HasForeignKey(c => c.AdId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Conversation>().HasIndex(c => new { c.SellerId, c.BuyerId, c.AdId }).IsUnique();
        modelBuilder.Entity<Conversation>().HasIndex(c => c.LastMessageTimestamp);

        // ── Notifications ──
        modelBuilder.Entity<Notification>().HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Notification>().HasOne(n => n.Ad).WithMany().HasForeignKey(n => n.AdId).OnDelete(DeleteBehavior.SetNull);

        // ── Outbox ──
        modelBuilder.Entity<OutboxMessage>().Property(o => o.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<OutboxMessage>().HasIndex(o => o.EventId).IsUnique();

        // ── Reviews ──
        modelBuilder.Entity<UserReview>().HasOne(r => r.Reviewer).WithMany(u => u.ReviewsWritten).HasForeignKey(r => r.ReviewerId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<UserReview>().HasOne(r => r.TargetUser).WithMany(u => u.ReviewsReceived).HasForeignKey(r => r.TargetUserId).OnDelete(DeleteBehavior.NoAction);

        // ── Locations ──
        modelBuilder.Entity<Location>().HasOne(l => l.Parent).WithMany(l => l.Children).HasForeignKey(l => l.ParentId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Location>().HasIndex(l => l.ParentId);
        modelBuilder.Entity<Location>().HasIndex(l => new { l.ParentId, l.Name }).IsUnique();

        // ── AuditLog ──
        modelBuilder.Entity<AuditLog>().HasOne(a => a.ActorUser).WithMany(u => u.AuditLogs).HasForeignKey(a => a.ActorUserId).OnDelete(DeleteBehavior.NoAction);

        // ── Reports ──
        modelBuilder.Entity<Report>().HasKey(r => r.Id);
        modelBuilder.Entity<Report>().Property(r => r.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<Report>().HasIndex(r => new { r.AdId, r.ReporterId }).IsUnique();
        modelBuilder.Entity<Report>().HasOne<Ad>().WithMany().HasForeignKey(r => r.AdId).OnDelete(DeleteBehavior.Cascade);
    }
}
