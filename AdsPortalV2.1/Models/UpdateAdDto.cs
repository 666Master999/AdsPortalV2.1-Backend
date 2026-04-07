namespace AdsPortalV2.Models;

public class UpdateAdDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public decimal? Price { get; set; }
    public bool? IsNegotiable { get; set; }
    public int? CategoryId { get; set; }
    // Clarify field to avoid collision with Location.Type
    public string? ListingType { get; set; }
    public int? LocationId { get; set; }
    public int? MainImageId { get; set; }
    public List<UpdateAdImageDto>? Images { get; set; }
}

public class UpdateAdImageDto
{
    public int? Id { get; set; }
    public bool Delete { get; set; }
    public string? FilePath { get; set; }
    public int? SortOrder { get; set; }
}
