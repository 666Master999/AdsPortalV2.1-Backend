using System.ComponentModel.DataAnnotations;

namespace AdsPortalV2.Entities;

public class Region
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public ICollection<City> Cities { get; set; } = new List<City>();
}
