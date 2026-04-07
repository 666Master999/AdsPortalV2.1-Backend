using System.ComponentModel.DataAnnotations;

namespace AdsPortalV2.Entities;

public class City
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int RegionId { get; set; }
    public Region? Region { get; set; }

    public ICollection<District> Districts { get; set; } = [];
}
