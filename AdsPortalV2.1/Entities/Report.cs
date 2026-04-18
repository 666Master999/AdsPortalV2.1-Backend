using System;

namespace AdsPortalV2.Entities;

public class Report
{
    public Guid Id { get; set; }
    public int AdId { get; set; }
    public int ReporterId { get; set; }
    public ReportReason Reason { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
