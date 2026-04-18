using System;

namespace AdsPortalV2.Entities;

public enum OutboxStatus { Pending = 0, Processing = 1, Done = 2, Failed = 3, DeadLetter = 4 }

public class OutboxMessage
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = null!;
    public string PayloadJson { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? AvailableAt { get; set; }
}
