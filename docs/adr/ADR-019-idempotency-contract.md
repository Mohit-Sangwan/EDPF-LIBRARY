# ADR-019: Idempotency contract

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Squad A lead

## Context

ADR-003 replaced cross-store transactions with an outbox delivering
at-least-once. At-least-once means duplicates, and the business requirement is
not "deliver once" but **effect once**: no duplicate charge, no duplicate
order, no duplicate medication administration record. Something must turn one
into the other.

## Options considered

1. **Exactly-once delivery.** Not achievable across a network partition;
   pursuing it produces complexity that fails anyway.
2. **Natural idempotency only** — design every operation so repetition is
   harmless. Works for some operations and not others; "administer 5mg" is
   not naturally idempotent.
3. **Idempotency keys with stored outcomes**, making replay observably
   identical to the original.

## Decision

Option 3.

- **Key format:** caller-supplied, opaque, ≤ 128 characters.
- **Scope:** per tenant **and** per operation. Tenant scoping is a security
  property, not bookkeeping: without it one tenant could replay another's
  operation by guessing a key.
- **Storage:** the same local transaction as the effect, so a recorded key
  and its effect cannot diverge.
- **Behaviour on replay:** return the **original** response; never
  re-execute. A replayed create returns the original 201 and the original
  entity id.
- **Same key, different payload:** `EDPF-TX-4002`, HTTP 409. The request hash
  is computed over the canonical bound DTO, so header noise and formatting
  differences do not defeat detection.
- **Retention:** bounded, and longer than the longest client retry window;
  expiry is a configuration decision per deployment.

The walking skeleton already demonstrates all three behaviours — replay,
conflict, and first-execution — as live gate checks.

## Consequences

- Positive: at-least-once delivery becomes effectively-once at the boundary
  that matters, with no distributed-transaction machinery.
- Negative: mutating endpoints must accept and honour the key; Phase 09 makes
  it mandatory rather than optional for them.
- Accepted risk: the idempotency store is on the write path and grows.
  Bounded retention plus tenant partitioning keeps it tractable.

## Revisit trigger

Idempotency-store contention appears in the Phase 31 write-latency budget, or
a consumer needs a key scope narrower than per-tenant-per-operation.
