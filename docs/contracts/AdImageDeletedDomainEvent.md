# AdImageDeletedDomainEvent

## Purpose
Triggered when main image of Ad is removed.

---

## Payload

```json
{
  "eventId": "guid",
  "adId": "int",
  "imageId": "int",
  "occurredAt": "datetime"
}
```

## Guarantees
- idempotent consumer required
- image deletion is best-effort
- event may be processed multiple times
- ordering is NOT guaranteed

## Producer rules
- must be emitted only from Ad aggregate
- must NOT be created in handlers directly

## Consumer rules
- must tolerate missing AdImage
- must not fail on already deleted file
- must be idempotent

---

# ⚠️ Why this matters

This protects against:

- silently adding/removing fields in the event payload
- changing ImageId -> ImageIds without migration
- handlers taking ownership and deleting Ad or other side-effects

These kinds of changes commonly break event-driven systems in production.

---

# Architecture status

| Layer | Status |
|------:|:------|
| correctness | tests |
| safety | CI |
| observability | metrics |
| stability | contracts (this file) |

---

# Next steps

After this contract is committed:

- Implement Outbox retry policy + dead-letter queue + poison message handling
- Add monitoring/alerts for OutboxLatency and DeadLetter growth

These are the final steps to reach production-grade guaranteed delivery.

---

# Notes

Emitters and consumers must treat this contract as immutable. Any change requires an explicit coordinated migration and cannot be made "silently".
