namespace AdsPortalV2.Services;

using AdsPortalV2.Entities;

public interface IFileStorage
{
    bool Exists(string relativePath);
    void Delete(string relativePath);
    bool TryNormalize(string relativePath, out string normalizedRelativePath);
    bool ExistsNormalized(string normalizedRelativePath);
    Task CreateConversationFoldersAsync(Conversation conv);
}
