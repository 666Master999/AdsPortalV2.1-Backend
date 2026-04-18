namespace AdsPortalV2.Entities;

public class AdAttributeValue
{
    public int Id { get; set; }
    public int AdId { get; set; }
    public Ad Ad { get; set; } = null!;
    public int AttributeId { get; set; }
    public CategoryAttribute Attribute { get; set; } = null!;
    public string Value { get; set; } = string.Empty;
}
