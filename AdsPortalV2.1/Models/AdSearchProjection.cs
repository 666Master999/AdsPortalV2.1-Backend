using AdsPortalV2.Entities;

namespace AdsPortalV2.Models;

public class AdSearchProjection
{
    public Ad Ad { get; set; } = null!;
    public double Score { get; set; }
}
