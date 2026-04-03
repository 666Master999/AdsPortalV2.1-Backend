using System.ComponentModel.DataAnnotations;

namespace AdsPortalV2.Entities;

public class District
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int CityId { get; set; }
    public City? City { get; set; }
}
