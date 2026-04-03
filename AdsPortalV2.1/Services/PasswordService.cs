using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace AdsPortalV2.Services;

public static class PasswordService
{
    private static readonly PasswordHasher<object> _hasher = new();

    public static string Hash(string password) =>
        _hasher.HashPassword(null!, password);

    public static bool Verify(string hashedPassword, string password)
    {
        try
        {
            if (_hasher.VerifyHashedPassword(null!, hashedPassword, password) != PasswordVerificationResult.Failed)
                return true;
        }
        catch { }
        return hashedPassword == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
    }

    public static bool IsLegacyHash(string hash) =>
        hash.Length == 64 && hash.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F'));
}
