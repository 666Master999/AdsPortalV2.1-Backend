Title: Outbox stabilization: aggregate-driven events, deterministic pipeline, GUID EventId, transactional consistency

Description

1. Problem

- Domain events were published manually from services/controllers (Publish calls).
- There was no single, aggregate-driven model for event generation.
- AdId in events could be incorrect (assigned after first SaveChanges but events created earlier).
- A SaveChanges interceptor introduced hidden/magic behaviour.
- Outbox ordering and idempotency were not deterministic.

2. Solution

- Events are emitted only by aggregates via Mark*/Raise methods (aggregate-driven events).
- Explicit pipeline enforced in application layer:
  1) SaveChanges #1 — persist entity state and obtain identity (Ad.Id)
  2) Aggregate.Mark*/Raise(...) — raise domain event(s) on aggregate
  3) MaterializeDomainEvents() — convert raised events to Outbox rows
  4) SaveChanges #2 — persist Outbox rows
- Interceptor removed / set to NOOP — no hidden logic in SaveChanges.
- EventId type changed to GUID and assigned inside Raise().
- Outbox is pure persistence layer (no domain logic embedded).

3. Database changes

- EventId migrated from bigint -> uniqueidentifier via safe migration (add Guid column default NEWID(), drop old column, rename, rebuild index).
- Indexes added:
  - IX_OutboxMessages_EventId (UNIQUE)
  - IX_OutboxMessages_CreatedAt

4. Guarantees

- Atomicity: two-phase persistence ensures either full commit (Ads + Outbox) or nothing — validated by integration tests.
- No Ads ↔ Outbox desync; AdId in payloads is correct (> 0).
- Idempotency via EventId (unique GUID) available to consumers.
- Deterministic pipeline — no interceptor magic.

5. Tests

- Integration tests added (SQLite in-memory) covering:
  1) success: Ad created and Outbox persisted with correct payload and non-empty EventId
  2) fail before outbox: rollback before second SaveChanges guarantees no Outbox created and no Ad persisted
  3) transaction rollback: explicit rollback ensures neither Ads nor Outbox rows remain

6. Breaking changes

- Event handlers may need to accept Guid EventId or use outbox message id; OutboxProcessor supports handlers with signature (TEvent, Guid) and (TEvent, long) and single-argument (TEvent) handlers for backward compatibility.

7. Deployment notes

- Run EF migration commands in staging/prod:
  dotnet ef migrations add Outbox_Final
  dotnet ef database update
- Verify database indices exist (IX_OutboxMessages_EventId unique, IX_OutboxMessages_CreatedAt).
- Ensure OutboxProcessor runs (prefer single active instance or use leader election).
- Monitor AttemptCount and DeadLetter queues; alert on growth.

Changelog

### Added
- Deterministic Outbox pipeline
- Integration tests for transactional consistency (SQLite)

### Changed
- EventId type: bigint -> Guid
- Event generation moved to aggregates (Mark*/Raise)

### Removed
- DomainEventPublisher / manual Publish calls
- Outbox interceptor materialization logic (moved to explicit MaterializeDomainEvents)

Notes

This PR finalizes the event pipeline: removed ad-hoc publishing, enforced aggregate-only event raising, and hardened the Outbox schema and processor for deterministic, idempotent delivery.
