using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("auth")]
public class AuthController(AppDbContext db, IConfiguration config, ILogger<AuthController> logger) : ControllerBase
{
    private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserLogin))
            return BadRequest(new ApiResponse { Success = false, Message = "Login cannot be empty" });

        if (string.IsNullOrWhiteSpace(request.UserPassword))
            return BadRequest(new ApiResponse { Success = false, Message = "Password cannot be empty" });

        if (await db.Users.AnyAsync(u => u.UserLogin == request.UserLogin))
            return BadRequest(new ApiResponse { Success = false, Message = "Login already registered" });

        var user = new User
        {
            UserLogin = request.UserLogin,
            UserPasswordHash = PasswordService.Hash(request.UserPassword)
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        logger.LogInformation("User registered: {Login} (id={Id})", user.UserLogin, user.Id);
        return Ok(AuthSuccess(user));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserLogin == request.UserLogin);
        if (user == null || !PasswordService.Verify(user.UserPasswordHash, request.UserPassword))
        {
            logger.LogWarning("Failed login attempt for: {Login}", request.UserLogin);
            return Unauthorized(new ApiResponse { Success = false, Message = "Invalid login or password" });
        }

        if (PasswordService.IsLegacyHash(user.UserPasswordHash))
        {
            user.UserPasswordHash = PasswordService.Hash(request.UserPassword);
            await db.SaveChangesAsync();
        }

        logger.LogInformation("User logged in: {Login} (id={Id})", user.UserLogin, user.Id);
        return Ok(AuthSuccess(user));
    }

    [HttpPost("logout")]
    public IActionResult Logout() => Ok(new ApiResponse { Success = true, Message = "Logged out successfully" });

    private ApiResponse<object> AuthSuccess(User user) => new()
    {
        Success = true,
        Message = "Authentication successful",
        Data = new
        {
            token = GenerateToken(user),
            userId = user.Id,
            userLogin = user.UserLogin,
            userName = user.UserName,
            avatar = user.AvatarPath
        }
    };

    private string GenerateToken(User user)
    {
        var credentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);


        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: [
                new Claim("id", user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.UserLogin),
                new Claim("isAdmin", user.IsAdmin.ToString())
            ],
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public record RegisterRequest(string UserLogin, string UserPassword);
public record LoginRequest(string UserLogin, string UserPassword);
