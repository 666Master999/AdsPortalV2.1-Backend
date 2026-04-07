using System.ComponentModel.DataAnnotations;

namespace AdsPortalV2.Entities;

public class Location
{
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public LocationType Type { get; set; }

    public int? ParentId { get; set; }
    public Location? Parent { get; set; }
    public ICollection<Location> Children { get; set; } = [];
}
