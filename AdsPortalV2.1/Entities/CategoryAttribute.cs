namespace AdsPortalV2.Entities;

public class CategoryAttribute
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AttributeType Type { get; set; }
    public bool IsRequired { get; set; }
    public bool IsFilter { get; set; }

    public List<CategoryAttributeOption> Options { get; set; } = [];
    public List<AdAttributeValue> AdValues { get; set; } = [];
}
