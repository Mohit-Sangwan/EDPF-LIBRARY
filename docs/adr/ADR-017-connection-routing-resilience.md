# ADR-017: Connection routing & resilience

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Squad A lead, Security Architect

## Context

Connections must be managed under load without exhausting the pool during an
incident, reads should use replicas where that is safe, failover must not lose
data, and residency (ADR-010) has to be enforced somewhere concrete.

## Options considered

1. **Reimplement pooling.** Full control; enormous cost, and ADO.NET pooling
   is mature, tuned and understood — a Principle 0 failure.
2. **Native pooling only, no routing.** Simple; forfeits replica reads, gives
   no failover policy, and leaves residency unenforced.
3. **Native pooling, with routing, health and resilience layered above it.**

## Decision

Option 3.

**Read/write split is opt-in per operation**, expressed as `ReadWriteIntent`.
Writes go to the primary. Reads may use a replica — *unless* the caller's
session holds a `SessionConsistencyToken` inside its staleness window, in
which case the read is pinned to the primary. Silent replica lag is a
correctness bug that surfaces as a support ticket months later ("the record I
just saved isn't there"), so read-your-own-writes is a first-class guarantee
rather than an option someone remembers to set.

**Region-pinned routing** implements ADR-010: tenant metadata carries the
region, the router refuses cross-region connections by default with
`EDPF-CMP-6002`, and an override requires break-glass and is audited. This is
the mechanism behind the GDPR and DPDP residency claims — without it those
claims are policy documents.

**Retry is restricted to provably transient error codes**, declared per
provider by `IDataProvider.IsTransient`. This is the load-bearing detail:
retrying a non-idempotent write on an ambiguous timeout is **data
corruption, not resilience**. An ambiguous outcome is never transient.
Retries carry exponential backoff with jitter, a circuit breaker per
endpoint, a timeout, and bulkhead isolation so one degraded shard cannot
exhaust the whole pool.

**Health monitoring** combines active probes with passive failure tracking,
and re-admits recovered replicas with hysteresis to prevent flapping.

## Consequences

- Positive: residency and read-your-own-writes are enforced structurally
  rather than by caller discipline.
- Negative: session tokens must flow with the caller's context; hosts that
  drop them silently lose the guarantee. Phase 07 carries them in the
  correlation context for that reason.
- Accepted risk: retry storms amplifying an outage — mitigated by jitter, the
  circuit breaker, and a global retry budget.

## Revisit trigger

A provider's transient-error classification proves wrong in production (a
retry corrupts data, or a genuinely transient fault is not retried); or a
topology appears that the router cannot express — multi-primary, or
geo-distributed writes.
