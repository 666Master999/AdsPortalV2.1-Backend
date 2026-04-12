namespace AdsPortalV2.Models;

public static class FilePathValidator
{
    public static bool TryValidate(string? input, string rootDirectory, out string relativePath, out string fullPath)
    {
        relativePath = string.Empty;
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var normalized = input.Trim().Replace('\\', '/');

        // Reject absolute URLs explicitly
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        // Allow paths that start with a leading slash (public URL style) by trimming the leading '/'
        if (normalized.StartsWith('/'))
            normalized = normalized.TrimStart('/');

        if (normalized.Contains("..") || Path.IsPathRooted(normalized))
            return false;

        var rootPath = Path.GetFullPath(rootDirectory);
        if (!rootPath.EndsWith(Path.DirectorySeparatorChar))
            rootPath += Path.DirectorySeparatorChar;

        fullPath = Path.GetFullPath(Path.Combine(rootDirectory, normalized));
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            return false;

        relativePath = normalized;
        return true;
    }
}
