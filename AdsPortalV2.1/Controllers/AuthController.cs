using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("auth")]
public class AuthController(AppDbContext db, IConfiguration config) : ControllerBase
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
            UserPasswordHash = HashPassword(request.UserPassword)
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return Ok(AuthSuccess(user));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserLogin == request.UserLogin);
        if (user == null || user.UserPasswordHash != HashPassword(request.UserPassword))
            return Unauthorized(new ApiResponse { Success = false, Message = "Invalid login or password" });

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
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string HashPassword(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
}

public record RegisterRequest(string UserLogin, string UserPassword);
public record LoginRequest(string UserLogin, string UserPassword);
