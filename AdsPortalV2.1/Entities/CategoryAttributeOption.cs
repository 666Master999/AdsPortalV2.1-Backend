namespace AdsPortalV2.Entities;

public class CategoryAttributeOption
{
    public int Id { get; set; }
    public int CategoryAttributeId { get; set; }
    public CategoryAttribute CategoryAttribute { get; set; } = null!;
    public string Value { get; set; } = string.Empty;
}
