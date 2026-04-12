using System.Security.Claims;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("auth")]
public class AuthController(AppDbContext db, ITokenService tokens, PermissionService perms, ILogger<AuthController> logger) : ControllerBase
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    [HttpPost("register")]
    public async Task<ActionResult<AuthSessionResponseDto>> Register([FromBody] RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.UserLogin) || string.IsNullOrWhiteSpace(req.UserPassword))
            return BadRequest(new ApiError("validation_error", "Login and password are required"));

        if (await db.Users.AnyAsync(u => u.UserLogin == req.UserLogin))
            return BadRequest(new ApiError("validation_error", "Login already registered"));

        var user = new User
        {
            UserLogin = req.UserLogin,
            UserPasswordHash = PasswordService.Hash(req.UserPassword)
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        logger.LogInformation("User registered: {Login} (id={Id})", user.UserLogin, user.Id);
        return Ok(await CreateSessionAsync(user));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthSessionResponseDto>> Login([FromBody] LoginRequest req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserLogin == req.UserLogin);
        if (user == null || !PasswordService.Verify(user.UserPasswordHash, req.UserPassword))
        {
            logger.LogWarning("Failed login: {Login}", req.UserLogin);
            return Unauthorized(new ApiError("validation_error", "Invalid login or password"));
        }

        if (await perms.HasActiveRestrictionAsync(user.Id, RestrictionType.LoginBan))
            return StatusCode(403, new ApiError("forbidden", "Account is banned"));

        logger.LogInformation("User logged in: {Login} (id={Id})", user.UserLogin, user.Id);
        return Ok(await CreateSessionAsync(user));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthRefreshResponseDto>> Refresh([FromBody] RefreshRequest req)
    {
        var hash = tokens.HashToken(req.RefreshToken);
        var session = await db.AuthSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash);

        if (session == null || session.IsRevoked || session.ExpiresAt < DateTime.UtcNow)
            return Unauthorized(new ApiError("validation_error", "Invalid or expired refresh token"));

        if (await perms.HasActiveRestrictionAsync(session.User.Id, RestrictionType.LoginBan))
            return StatusCode(403, new ApiError("forbidden", "Account is banned"));

        var newRefresh = tokens.GenerateRefreshToken();
        session.RefreshTokenHash = tokens.HashToken(newRefresh);
        session.LastActivityAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(new AuthRefreshResponseDto(tokens.GenerateAccessToken(session.User, session), newRefresh));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<ActionResult> Logout()
    {
        if (!User.TryGetSessionId(out var sessionId))
            return Unauthorized();

        var session = await db.AuthSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.IsRevoked = true;
            session.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        return Ok();
    }

    [Authorize]
    [HttpPost("logout-all")]
    public async Task<ActionResult> LogoutAll()
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        var now = DateTime.UtcNow;
        await db.AuthSessions
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsRevoked, true)
                .SetProperty(x => x.RevokedAt, now));

        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenVersion, u => u.TokenVersion + 1));

        return Ok();
    }

    [Authorize]
    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyCollection<AuthSessionDto>>> GetSessions()
    {
        if (!User.TryGetUserId(out var userId) || !User.TryGetSessionId(out var currentSessionId))
            return Unauthorized();

        var sessions = await db.AuthSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && !s.IsRevoked && s.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(s => s.LastActivityAt)
            .Select(s => new AuthSessionDto(s.Id, s.DeviceName, s.IpAddress, s.LastActivityAt, s.CreatedAt, s.Id == currentSessionId))
            .ToListAsync();

        return Ok(sessions);
    }

    [Authorize]
    [HttpDelete("sessions/{id:guid}")]
    public async Task<ActionResult> RevokeSession(Guid id)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        var session = await db.AuthSessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (session == null)
            return NotFound();

        session.IsRevoked = true;
        session.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok();
    }

    [Authorize]
    [HttpGet("/me/restrictions")]
    public async Task<ActionResult<IReadOnlyCollection<MeRestrictionDto>>> GetMyRestrictions()
    {
        if (!User.TryGetUserId(out var userId)) return Unauthorized();
        var ctx = await perms.GetUserContextAsync(userId);
        return Ok(ctx.Restrictions.Select(r => new MeRestrictionDto(r.Type.ToString(), r.ExpiresAt, r.Reason)).ToList());
    }

    private async Task<AuthSessionResponseDto> CreateSessionAsync(User user)
    {
        var refreshToken = tokens.GenerateRefreshToken();
        var userAgent = Request.Headers.UserAgent.ToString();

        var session = new UserSession
        {
            UserId = user.Id,
            RefreshTokenHash = tokens.HashToken(refreshToken),
            DeviceName = string.IsNullOrEmpty(userAgent) ? null : userAgent[..Math.Min(userAgent.Length, 200)],
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent[..Math.Min(userAgent.Length, 512)],
            ExpiresAt = DateTime.UtcNow.Add(RefreshTokenLifetime)
        };

        db.AuthSessions.Add(session);
        await db.SaveChangesAsync();

        return new AuthSessionResponseDto(tokens.GenerateAccessToken(user, session), refreshToken, user.Id, user.UserLogin, user.UserName, user.AvatarPath);
    }
}

public record RegisterRequest(string UserLogin, string UserPassword);
public record LoginRequest(string UserLogin, string UserPassword);
public record RefreshRequest(string RefreshToken);

