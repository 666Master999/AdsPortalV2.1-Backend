namespace AdsPortalV2.Entities;

public class Ad
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int CategoryId { get; set; }
    public int CityId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public Category Category { get; set; } = null!;
    public City City { get; set; } = null!;
    public List<AdImage> Images { get; set; } = [];
    public List<ChatThread> ChatThreads { get; set; } = [];
    public List<Favorite> Favorites { get; set; } = [];
    public List<Complaint> Complaints { get; set; } = [];
}
