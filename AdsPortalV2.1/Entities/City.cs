namespace AdsPortalV2.Entities;

public class City
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public List<Ad> Ads { get; set; } = [];
}
