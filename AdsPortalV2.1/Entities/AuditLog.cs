namespace AdsPortalV2.Entities;

public class AuditLog
{
    public int Id { get; set; }
    public int ActorUserId { get; set; }
    public int? TargetUserId { get; set; }
    public long? OutboxMessageId { get; set; }
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public int? TargetId { get; set; }
    public string? Reason { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public User ActorUser { get; set; } = null!;
}
