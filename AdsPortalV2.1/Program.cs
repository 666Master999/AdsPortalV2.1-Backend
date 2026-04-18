using AdsPortalV2;
using AdsPortalV2.Entities;
using AdsPortalV2.Controllers;
using AdsPortalV2.Extensions;
using AdsPortalV2.Data;
using AdsPortalV2.Hubs;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.Converters.Add(new CamelCaseEnumConverterFactory());
    });

builder.Services
    .AddSignalR()
    .AddJsonProtocol(o =>
    {
        o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.PayloadSerializerOptions.Converters.Add(new CamelCaseEnumConverterFactory());
    });

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200_000_000);

builder.Services.AddMemoryCache();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddResponseCompression();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SupportNonNullableReferenceTypes();
    options.OperationFilter<DefaultErrorResponsesFilter>();
    options.SchemaFilter<EnumSchemaFilter>();

    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AdsPortal API",
        Version = "v1"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Bearer {token}"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
// controllers configured earlier with centralized JsonSerializerOptions

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

builder.Services.AddSingleton<OnlineUserTracker>();
builder.Services.AddHostedService<PresenceCleanupService>();
builder.Services.AddScoped<IBlockService, BlockService>();
builder.Services.AddScoped<ImageService>();
builder.Services.AddScoped<MessageFlowService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<IConversationRepository, EfConversationRepository>();
builder.Services.AddScoped<IUserRepository, EfUserRepository>();
builder.Services.AddScoped<MessagePipeline>();
builder.Services.AddScoped<IMessageMiddleware, BlockMiddleware>();
builder.Services.AddScoped<AdQueryService>();
builder.Services.AddScoped<AdVisibilityService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IAdDetailsService, AdDetailsService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<IFileStorage, FileStorage>();
builder.Services.AddScoped<IAdImagePatchService, AdImagePatchService>();
builder.Services.AddScoped<INotificationFactory, NotificationFactory>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IModerationService, ModerationService>();
builder.Services.AddScoped<DomainEventPublisher>();
builder.Services.AddScoped<IDomainEventPublisher>(sp => sp.GetRequiredService<DomainEventPublisher>());
builder.Services.AddScoped<IDomainEventBuffer>(sp => sp.GetRequiredService<DomainEventPublisher>());
// Outbox infrastructure
builder.Services.AddHostedService<AdsPortalV2.Services.Outbox.OutboxProcessor>();
// domain event handlers
builder.Services.AddScoped<AdsPortalV2.Services.Handlers.AuditHandler>();
builder.Services.AddScoped<AdsPortalV2.Services.Handlers.NotificationHandler>();
builder.Services.AddScoped<AdsPortalV2.Services.Handlers.FileDeletionHandler>();
// register handlers for DI resolution by generic interface
builder.Services.AddScoped<IDomainEventHandler<AdApproved>>(sp => sp.GetRequiredService<AdsPortalV2.Services.Handlers.AuditHandler>());
builder.Services.AddScoped<IDomainEventHandler<AdRejected>>(sp => sp.GetRequiredService<AdsPortalV2.Services.Handlers.AuditHandler>());
builder.Services.AddScoped<IDomainEventHandler<AdApproved>>(sp => sp.GetRequiredService<AdsPortalV2.Services.Handlers.NotificationHandler>());
builder.Services.AddScoped<IDomainEventHandler<AdRejected>>(sp => sp.GetRequiredService<AdsPortalV2.Services.Handlers.NotificationHandler>());
builder.Services.AddScoped<IDomainEventHandler<FileDeletionRequested>>(sp => sp.GetRequiredService<AdsPortalV2.Services.Handlers.FileDeletionHandler>());
builder.Services.AddSingleton<IAuthorizationHandler, PermissionRequirementHandler>();
builder.Services.AddScoped<IAuthorizationHandler, CanEditAdHandler>();
builder.Services.AddScoped<IAuthorizationHandler, CanDeleteAdHandler>();
builder.Services.AddSingleton<DialogWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DialogWriterService>());
builder.Services.AddScoped<DialogReaderService>();
// Snapshot background worker
builder.Services.AddHostedService<SnapshotBackgroundService>();

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
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    // Serve Swagger UI at application root
    c.RoutePrefix = string.Empty;
    // label shown in the UI header and the select box
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "AdsPortal API v1");
});
app.MapHealthChecks("/health");
app.MapHub<SystemNotificationHub>("/hubs/notifications");
app.MapHub<ChatHub>("/hubs/chat");

// Seed the database with default data
app.SeedDatabase();

app.Run();
