using AdsPortalV2.Models;

namespace AdsPortalV2.Services;

public class FileStorage : IFileStorage
{
    private const string Root = "wwwroot";

    public bool Exists(string relativePath)
    {
        if (!TryNormalize(relativePath, out var normalizedRelativePath))
            return false;

        return ExistsNormalized(normalizedRelativePath);
    }

    public void Delete(string relativePath)
    {
        if (!TryNormalize(relativePath, out var normalizedRelativePath))
            return;

        var fullPath = Path.Combine(Root, normalizedRelativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    public bool ExistsNormalized(string normalizedRelativePath)
        => File.Exists(Path.Combine(Root, normalizedRelativePath));

    public bool TryNormalize(string relativePath, out string normalizedRelativePath)
    {
        normalizedRelativePath = string.Empty;
        if (!FilePathValidator.TryValidate(relativePath, Root, out _, out var fullPath))
            return false;

        normalizedRelativePath = Path.GetRelativePath(Root, fullPath).Replace('\\', '/');
        return true;
    }
}
