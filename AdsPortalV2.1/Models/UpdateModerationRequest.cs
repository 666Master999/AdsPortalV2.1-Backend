using System.ComponentModel.DataAnnotations;

namespace AdsPortalV2.Models;

public class UpdateModerationRequest
{
    [Required]
    public AdsPortalV2.Entities.AdStatus Status { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }
}
