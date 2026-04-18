using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace AdsPortalV2.Services;

public interface IDomainEvent
{
    Guid EventId { get; set; }
    DateTime OccurredAt { get; }
}

public abstract class Entity
{
    private readonly List<IDomainEvent> _events = [];

    public List<IDomainEvent> DomainEvents => _events;

    protected void Raise(IDomainEvent @event)
    {
        try
        {
            // assign canonical event id at the point of raising
            @event.EventId = Guid.NewGuid();
        }
        catch { }

        _events.Add(@event);
    }
}

public record UserBanned(int UserId, int ActorId) : IDomainEvent
{
    // EventId assigned when event is raised by the aggregate
    public Guid EventId { get; set; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

public record AdCreated(int AdId, int AuthorId) : IDomainEvent
{
    public Guid EventId { get; set; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

public record AdApproved(int AdId, int ActorId, string? ActorName = null, string? OldValue = null, string? NewValue = null) : IDomainEvent
{
    public Guid EventId { get; set; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

public record AdRejected(int AdId, int ActorId, string? ActorName = null, string? Reason = null, string? OldValue = null, string? NewValue = null) : IDomainEvent
{
    public Guid EventId { get; set; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

public record FileDeletionRequested(string[] FilePaths) : IDomainEvent
{
    public Guid EventId { get; set; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

public record RoleAssigned(int UserId, string Role, int ActorId) : IDomainEvent
{
    public Guid EventId { get; set; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

public interface IDomainEventHandler<TEvent> where TEvent : IDomainEvent
{
    Task Handle(TEvent @event, long outboxMessageId);
}

public interface IDomainEventPublisher
{
    void Publish(IDomainEvent @event);
}

public interface IDomainEventBuffer : IDomainEventPublisher
{
    IReadOnlyCollection<IDomainEvent> PendingEvents { get; }

    void Clear();
}

public class DomainEventPublisher : IDomainEventBuffer
{
    private readonly List<IDomainEvent> _pendingEvents = [];
    private readonly ILogger<DomainEventPublisher> _logger;

    public DomainEventPublisher(ILogger<DomainEventPublisher> logger)
    {
        _logger = logger;
    }

    public IReadOnlyCollection<IDomainEvent> PendingEvents => _pendingEvents;

    public void Publish(IDomainEvent @event)
    {
        try { _logger.LogWarning("DOMAIN EVENT BUFFERED: {Event}", @event.GetType().FullName); } catch { }
        _pendingEvents.Add(@event);
    }

    public void Clear()
    {
        _pendingEvents.Clear();
    }
}
