using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AdsPortalV2.Entities;
using Microsoft.IdentityModel.Tokens;

namespace AdsPortalV2.Services;

public interface ITokenService
{
    string GenerateAccessToken(User user, UserSession session);
    string GenerateRefreshToken();
    string HashToken(string token);
}

public class TokenService(IConfiguration config) : ITokenService
{
    private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
    private readonly int _expiryMinutes = int.TryParse(config["Jwt:ExpiryMinutes"], out var m) ? m : 60;

    public string GenerateAccessToken(User user, UserSession session)
    {
        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("sid", session.Id.ToString()),
            new Claim("ver", user.TokenVersion.ToString())
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_expiryMinutes),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
