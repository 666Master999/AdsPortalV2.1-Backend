namespace AdsPortalV2.Entities;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }

    public Category? Parent { get; set; }
    public List<Category> Children { get; set; } = [];
    public List<Ad> Ads { get; set; } = [];
}
