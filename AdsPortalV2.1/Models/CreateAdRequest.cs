using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;

namespace AdsPortalV2.Models;

public class CreateAdRequest
{
    [Required]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public decimal? Price { get; set; }

    public bool? IsNegotiable { get; set; }

    [Required]
    public int? CategoryId { get; set; }

    public string? ListingType { get; set; }

    [Required]
    public int? LocationId { get; set; }

    // Files are bound separately in controller; ignore in JSON serialization
    [JsonIgnore]
    public List<IFormFile>? Files { get; set; }
}
