namespace AdsPortalV2.Models;

public static class FilePathHelpers
{
    public static string EnsurePublicPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path!;

        return path.StartsWith('/') ? path! : "/" + path;
    }
}
