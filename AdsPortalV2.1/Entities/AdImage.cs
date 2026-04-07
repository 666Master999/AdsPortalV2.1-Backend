namespace AdsPortalV2.Entities;

public class AdImage
{
    public int Id { get; set; }
    public int AdId { get; set; }
    public string FilePath { get; set; } = "";
    public int SortOrder { get; set; }

    public Ad Ad { get; set; } = null!;
}