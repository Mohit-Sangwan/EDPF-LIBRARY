# ADR-003: Consistency — outbox + saga + idempotency, no cross-store 2PC

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Data squad lead

## Context

The original spec assumed "distributed transactions" across heterogeneous
stores (SQL Server, Cosmos, MongoDB, Oracle). Cross-store two-phase commit is
partly infeasible there (Gap #5, severity C): several target stores expose no
XA-compatible coordinator, and "portable nested transactions" do not exist
(SQL Server savepoints are not real nesting).

## Options considered

1. **2PC/XA via a distributed transaction coordinator.** Only works for the
   subset of stores that support it; couples availability to the coordinator;
   Spike-D (attempting a transaction spanning SQL Server and MongoDB)
   documents the failure and closes the question permanently.
2. **Best-effort dual writes.** Simple and silently wrong: partial failure
   leaves stores diverged with no record.
3. **Local ACID + transactional outbox + saga/process manager + idempotency
   keys, with DLQ and bounded retry.** One ACID transaction per store; cross-
   store effects ride an outbox row committed atomically with the state
   change; consumers are idempotent; multi-step flows are sagas with explicit
   compensation.

## Decision

Option 3. Savepoints are used where the provider capability declares support;
"nested transactions" is not a portable promise and is never offered as one.
Exactly-once **effect** (not delivery) is the contract: at-least-once
delivery + idempotent consumers. The walking skeleton proves the mechanism at
smallest scale: `PatientCreated` rides an outbox row in the create
transaction and is dispatched exactly once (gate demonstration 7).

## Consequences

- Positive: works uniformly across all thirteen target engines; failure
  modes are explicit rows, not distributed limbo.
- Negative: consumers must tolerate eventual consistency; saga compensation
  is application logic that must be designed, not inherited.
- Accepted risk: outbox dispatch lag under load — measured in Z.18
  (`outbox dispatch lag`), budgeted in Phase 30 SLOs.

## Revisit trigger

A design partner requires synchronous cross-store atomicity for a flow where
compensation is provably impossible — escalates to TSC with the Spike-D
evidence attached.
