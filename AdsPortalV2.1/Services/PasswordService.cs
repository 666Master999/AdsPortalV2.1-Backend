using System.Security.Cryptography;
using System.Text;

namespace AdsPortalV2.Services
{
    public static class PasswordService
    {
        public static string Hash(string password) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
    }
}
