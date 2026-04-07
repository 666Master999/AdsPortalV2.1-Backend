namespace AdsPortalV2.Models;

public sealed class ImagePatchContext
{
    public bool Delete { get; init; }
    public int? Id { get; init; }
    public string? FilePath { get; init; }
    public int? SortOrder { get; init; }
    public bool HasId => Id.HasValue;
}
