namespace AdsPortalV2.Entities;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public bool IsLeaf { get; set; } = true;
    public string? Path { get; set; }

    public Category? Parent { get; set; }
    public List<Category> Children { get; set; } = new List<Category>();
    public List<CategoryAttribute> Attributes { get; set; } = new List<CategoryAttribute>();
    public List<Ad> Ads { get; set; } = new List<Ad>();
}
