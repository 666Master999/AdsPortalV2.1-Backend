namespace AdsPortalV2.Models;

public class AdsQuery
{
    public string? Search { get; set; }
    public string? Location { get; set; }
    public string? Category { get; set; }
    public bool IncludeChildren { get; set; } = false;
    public decimal? PriceFrom { get; set; }
    public decimal? PriceTo { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public int? UserId { get; set; }
    public string? Status { get; set; }
    public string? Type { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Sort { get; set; }
    public string? Cursor { get; set; }
}
