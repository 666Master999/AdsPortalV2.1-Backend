using System.Net;
using System.Text.Json;
using AdsPortalV2.Models;

namespace AdsPortalV2;

public class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-Id"] = correlationId;
            return Task.CompletedTask;
        });

        try
        {
            await next(context);

            if (context.Response.StatusCode >= 400 && context.Response.StatusCode < 500 && logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("[{CId}] HTTP {Status} {Method} {Path}",
                    correlationId, context.Response.StatusCode, context.Request.Method, context.Request.Path);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex, "[{CId}] Unhandled exception on {Method} {Path}",
                    correlationId, context.Request.Method, context.Request.Path);

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            var body = new ApiError(
                "internal_error",
                context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment()
                    ? ex.Message
                    : "Внутренняя ошибка сервера.",
                new { correlationId });

            await context.Response.WriteAsync(JsonSerializer.Serialize(body, _jsonOptions));
        }
    }
}
