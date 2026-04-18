using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdsPortalV2.IntegrationTests;

public class AdOutboxIntegrationTests
{
    [Fact]
    public async Task CreateAd_Success_CreatesOutbox()
    {
        var _ctx = TestHelpers.CreateInMemorySqliteContextOptions();
        using var connection = _ctx.Item1;
        var options = _ctx.Item2;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await using var tx = await db.Database.BeginTransactionAsync();

        // create required FK rows
        var user = new AdsPortalV2.Entities.User { UserLogin = "u1", UserPasswordHash = "h", CreatedAt = DateTime.UtcNow };
        var location = new AdsPortalV2.Entities.Location { Name = "loc", Type = AdsPortalV2.Entities.LocationType.Region };
        db.Users.Add(user);
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var ad = new Ad
        {
            Title = "Test ad",
            Description = "desc",
            Price = 10,
            IsNegotiable = false,
            CategoryId = null,
            ListingType = null,
            LocationId = location.Id,
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = AdStatus.Active
        };

        db.Ads.Add(ad);
        await db.SaveChangesAsync(); // assign Id

        ad.MarkCreated(ad.UserId);
        db.MaterializeDomainEvents();
        await db.SaveChangesAsync(); // persist outbox
        db.ClearDomainEvents();

        await tx.CommitAsync();

        var savedAd = await db.Ads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == ad.Id);
        Assert.NotNull(savedAd);

        var outbox = await db.OutboxMessages.AsNoTracking().FirstOrDefaultAsync(o => o.EventId != default);
        Assert.NotNull(outbox);

        var payloadDoc = JsonSerializer.Deserialize<AdsPortalV2.Services.AdCreated>(outbox.PayloadJson);
        Assert.Equal(ad.Id, payloadDoc.AdId);
        Assert.NotEqual(Guid.Empty, outbox.EventId);
    }

    [Fact]
    public async Task CreateAd_FailBeforeOutbox_NoOutboxCreated()
    {
        var _ctx = TestHelpers.CreateInMemorySqliteContextOptions();
        using var connection = _ctx.Item1;
        var options = _ctx.Item2;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Simulate transaction where second SaveChanges fails
        await using var tx = await db.Database.BeginTransactionAsync();

        // create required FK rows
        var user = new AdsPortalV2.Entities.User { UserLogin = "u2", UserPasswordHash = "h", CreatedAt = DateTime.UtcNow };
        var location = new AdsPortalV2.Entities.Location { Name = "loc2", Type = AdsPortalV2.Entities.LocationType.Region };
        db.Users.Add(user);
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var ad = new Ad
        {
            Title = "Test ad fail",
            Description = "desc",
            Price = 10,
            IsNegotiable = false,
            CategoryId = null,
            ListingType = null,
            LocationId = location.Id,
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = AdStatus.Active
        };

        db.Ads.Add(ad);
        await db.SaveChangesAsync(); // assign Id

        ad.MarkCreated(ad.UserId);
        db.MaterializeDomainEvents();

        // simulate failure before persisting outbox
        await tx.RollbackAsync();

        // After rollback, Outbox should not contain event
        var outboxCount = await db.OutboxMessages.CountAsync();
        Assert.Equal(0, outboxCount);

        // And ad should not be present (rolled back)
        var adCount = await db.Ads.CountAsync(a => a.Id == ad.Id);
        Assert.Equal(0, adCount);
    }

    [Fact]
    public async Task TransactionRollback_AllRolledBack()
    {
        var _ctx = TestHelpers.CreateInMemorySqliteContextOptions();
        using var connection = _ctx.Item1;
        var options = _ctx.Item2;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // begin transaction and then throw
        await using var tx = await db.Database.BeginTransactionAsync();

        // create required FK rows
        var user = new AdsPortalV2.Entities.User { UserLogin = "u3", UserPasswordHash = "h", CreatedAt = DateTime.UtcNow };
        var location = new AdsPortalV2.Entities.Location { Name = "loc3", Type = AdsPortalV2.Entities.LocationType.Region };
        db.Users.Add(user);
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var ad = new Ad
        {
            Title = "Test ad tx",
            Description = "desc",
            Price = 10,
            IsNegotiable = false,
            CategoryId = null,
            ListingType = null,
            LocationId = location.Id,
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = AdStatus.Active
        };

        db.Ads.Add(ad);
        await db.SaveChangesAsync(); // assign Id

        ad.MarkCreated(ad.UserId);
        db.MaterializeDomainEvents();
        await db.SaveChangesAsync(); // persist outbox
        db.ClearDomainEvents();

        // now force rollback by disposing without commit
        await tx.RollbackAsync();

        var adExists = await db.Ads.AnyAsync(a => a.Id == ad.Id);
        var outboxExists = await db.OutboxMessages.AnyAsync(o => o.EventId != default);

        Assert.False(adExists);
        Assert.False(outboxExists);
    }
}
