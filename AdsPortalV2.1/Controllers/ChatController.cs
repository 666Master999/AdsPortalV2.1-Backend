using System.Security.Claims;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("chat")]
[Authorize]
public class ChatController(AppDbContext db) : ControllerBase
{
    [HttpGet("thread/{adId}")]
    public async Task<IActionResult> GetThread(int adId)
    {
        var userId = GetUserId();
        var thread = await db.ChatThreads
            .FirstOrDefaultAsync(t => t.AdId == adId && t.BuyerId == userId);

        return thread == null ? NotFound() : Ok(thread);
    }

    [HttpGet("messages")]
    public async Task<IActionResult> GetMessages([FromQuery] int threadId) =>
        Ok(await db.ChatMessages
            .Where(m => m.ChatThreadId == threadId)
            .OrderBy(m => m.SentAt)
            .ToListAsync());

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] ChatMessage message)
    {
        message.SenderId = GetUserId();
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();
        return Ok(message);
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromQuery] int messageId)
    {
        var message = await db.ChatMessages.FindAsync(messageId);
        if (message == null) return NotFound();

        db.ChatMessages.Remove(message);
        await db.SaveChangesAsync();
        return Ok();
    }

    private int GetUserId() =>
        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}
