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

    public Task CreateConversationFoldersAsync(AdsPortalV2.Entities.Conversation conv)
    {
        var webRoot = Root;
        var dialogFolder = conv.DialogFolderPath ?? $"files/{conv.SellerId}/Ads/{conv.AdId}/dialogs/{conv.Id}";
        var attachFolder = Path.Combine(webRoot, dialogFolder.TrimStart('/').Replace('/', Path.DirectorySeparatorChar), "attachments");
        Directory.CreateDirectory(Path.Combine(webRoot, dialogFolder.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(attachFolder);
        return Task.CompletedTask;
    }
}
