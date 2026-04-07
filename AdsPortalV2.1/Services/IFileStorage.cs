namespace AdsPortalV2.Services;

public interface IFileStorage
{
    bool Exists(string relativePath);
    void Delete(string relativePath);
    bool TryNormalize(string relativePath, out string normalizedRelativePath);
    bool ExistsNormalized(string normalizedRelativePath);
}
