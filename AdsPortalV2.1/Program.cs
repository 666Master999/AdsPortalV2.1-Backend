using AdsPortalV2;
using AdsPortalV2.Entities;
using AdsPortalV2.Controllers;
using AdsPortalV2.Data;
using AdsPortalV2.Hubs;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000);

builder.Services.AddMemoryCache();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddResponseCompression();
builder.Services.AddOpenApi();
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        // Use camelCase enum strings so frontend always receives predictable lowercase values
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    });

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var key = Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!);
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidateAudience = false,
            ValidateIssuer = false,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
            OnTokenValidated = async ctx =>
            {
                var sub = ctx.Principal?.FindFirstValue("sub");
                var sid = ctx.Principal?.FindFirstValue("sid");
                var ver = ctx.Principal?.FindFirstValue("ver");

                if (!int.TryParse(sub, out var userId) ||
                    !Guid.TryParse(sid, out var sessionId) ||
                    !int.TryParse(ver, out var version))
                {
                    ctx.Fail("invalid_token");
                    return;
                }

                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

                var session = await db.AuthSessions
                    .AsNoTracking()
                    .Include(s => s.User)
                    .FirstOrDefaultAsync(s => s.Id == sessionId);

                if (session == null || session.IsRevoked || session.ExpiresAt < DateTime.UtcNow)
                {
                    ctx.Fail("session_invalid");
                    return;
                }

                var user = session.User;
                if (user.Id != userId || user.TokenVersion != version)
                {
                    ctx.Fail("token_revoked");
                    return;
                }

                // Load UserContext (cached) and inject roles/permissions as claims
                var permService = ctx.HttpContext.RequestServices.GetRequiredService<PermissionService>();
                var uctx = await permService.GetUserContextAsync(userId);

                // LoginBan ? reject token
                if (uctx.Restrictions.Any(r => r.Type == RestrictionType.LoginBan))
                {
                    ctx.Fail("account_banned");
                    return;
                }

                var identity = ctx.Principal?.Identity as ClaimsIdentity;
                foreach (var role in uctx.Roles)
                    identity?.AddClaim(new Claim(ClaimTypes.Role, role));
                foreach (var perm in uctx.Permissions)
                    identity?.AddClaim(new Claim("perm", perm));
            }
        };
    });

var authorization = builder.Services.AddAuthorizationBuilder();
authorization.AddPolicy(AuthorizationPolicies.CanViewHiddenAd, p => p.RequireAssertion(ctx =>
    ctx.User.Claims.Any(c => c.Type == "perm" && c.Value == "ads.view_hidden") &&
    ctx.User.Claims.Any(c => c.Type == ClaimTypes.Role && (c.Value == "Moderator" || c.Value == "Admin" || c.Value == "SuperAdmin"))));
authorization.AddPolicy(AuthorizationPolicies.CanModerateAd, p => p.Requirements.Add(new PermissionRequirement("ads.moderate")));
authorization.AddPolicy(AuthorizationPolicies.CanBanUser, p => p.Requirements.Add(new PermissionRequirement("users.ban")));
authorization.AddPolicy("CanUnbanUser", p => p.Requirements.Add(new PermissionRequirement("users.unban")));
authorization.AddPolicy("CanAssignRole", p => p.Requirements.Add(new PermissionRequirement("roles.assign")));
authorization.AddPolicy("CanRevokeRole", p => p.Requirements.Add(new PermissionRequirement("roles.revoke")));
authorization.AddPolicy("CanViewLogs", p => p.Requirements.Add(new PermissionRequirement("logs.view")));
authorization.AddPolicy("CanManageCategories", p => p.RequireAssertion(ctx =>
    ctx.User.Claims.Any(c => c.Type == ClaimTypes.Role && (c.Value == "Admin" || c.Value == "SuperAdmin"))));
authorization.AddPolicy(AuthorizationPolicies.CanEditAd, p => p.Requirements.Add(new CanEditAdRequirement()));
authorization.AddPolicy(AuthorizationPolicies.CanDeleteAd, p => p.Requirements.Add(new CanDeleteAdRequirement()));

builder.Services.AddCustomCors(builder.Configuration);

builder.Services.AddSignalR();
builder.Services.AddSingleton<OnlineUserTracker>();
builder.Services.AddHostedService<PresenceCleanupService>();
builder.Services.AddScoped<ImageService>();
builder.Services.AddScoped<AdQueryService>();
builder.Services.AddScoped<AdVisibilityService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<IFileStorage, FileStorage>();
builder.Services.AddScoped<IAdImagePatchService, AdImagePatchService>();
builder.Services.AddScoped<INotificationFactory, NotificationFactory>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddSingleton<IDomainEventPublisher, DomainEventPublisher>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionRequirementHandler>();
builder.Services.AddScoped<IAuthorizationHandler, CanEditAdHandler>();
builder.Services.AddScoped<IAuthorizationHandler, CanDeleteAdHandler>();
builder.Services.AddSingleton<DialogWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DialogWriterService>());
builder.Services.AddScoped<DialogReaderService>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 190, Window = TimeSpan.FromMinutes(1) }));

    var createAdLimit = builder.Configuration.GetValue<int?>("RateLimits:CreateAdPerDay") ?? 25;
    var messagesLimit = builder.Configuration.GetValue<int?>("RateLimits:MessagesPerSecond") ?? 5;

    options.AddPolicy("CreateAdPerDay", ctx =>
    {
        var key = ctx.User.TryGetUserId(out var uid)
            ? $"user:{uid}"
            : $"ip:{ctx.Connection.RemoteIpAddress}";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = createAdLimit,
            Window = TimeSpan.FromDays(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("MessagesPerSecond", ctx =>
    {
        var key = ctx.User.TryGetUserId(out var uid)
            ? $"user:{uid}"
            : $"ip:{ctx.Connection.RemoteIpAddress}";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = messagesLimit,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

var app = builder.Build();

app.UseMiddleware<ExceptionMiddleware>();
app.UseResponseCompression();

app.UseCors("AllowFrontend");

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ActivityMiddleware>();

app.UseStaticFiles();

app.MapControllers();
app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHub<OnlineHub>("/hubs/online");

// Seed the database with default data
app.SeedDatabase();

app.Run();
