using AdsPortalV2;
using AdsPortalV2.Data;
using AdsPortalV2.Hubs;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var key = Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddCustomCors(builder.Configuration);

builder.Services.AddSignalR();
builder.Services.AddSingleton<OnlineUserTracker>();
builder.Services.AddHostedService<PresenceCleanupService>();
builder.Services.AddScoped<ImageService>();
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

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

// Seed the database with default data
app.SeedDatabase();

app.Run();
