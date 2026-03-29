using AdsPortalV2.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("test")]
public class TestController(AppDbContext db, IWebHostEnvironment env) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var databaseIsConnected = false;
        var databaseError = (string?)null;

        try
        {
            databaseIsConnected = await db.Database.CanConnectAsync();
        }
        catch (Exception ex)
        {
            databaseError = ex.Message;
        }

        return Ok(new
        {
            checks = new[]
            {
                new Dictionary<string, object> { ["server is wirkin"] = true },
                new Dictionary<string, object> { ["database is connected"] = databaseIsConnected }
            },
            diagnostics = new
            {
                environment = env.EnvironmentName,
                requestUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}",
                pathBase = Request.PathBase.Value ?? string.Empty,
                utcTime = DateTime.UtcNow.ToString("O"),
                databaseProvider = db.Database.ProviderName ?? "unknown",
                applicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                databaseError
            }
        });
    }
}
