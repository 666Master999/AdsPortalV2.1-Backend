namespace AdsPortalV2.Services;

public interface IDomainEvent { }

public record UserBanned(int UserId, int ActorId) : IDomainEvent;
public record AdCreated(int AdId, int AuthorId) : IDomainEvent;
public record AdApproved(int AdId, int ActorId) : IDomainEvent;
public record AdRejected(int AdId, int ActorId, string? Reason) : IDomainEvent;
public record RoleAssigned(int UserId, string Role, int ActorId) : IDomainEvent;

public interface IDomainEventPublisher
{
    void Publish(IDomainEvent @event);
}

public class DomainEventPublisher(ILogger<DomainEventPublisher> logger) : IDomainEventPublisher
{
    public void Publish(IDomainEvent @event) =>
        logger.LogInformation("DomainEvent: {Event}", @event);
}
