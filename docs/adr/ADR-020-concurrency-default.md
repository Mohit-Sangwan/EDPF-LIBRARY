# ADR-020: Concurrency default

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Squad A lead, Compliance Officer

## Context

Two clinicians open the same patient record and both save. The original
specification was silent on what happens — which in practice means the second
write overwrites the first and nobody is told. That is a lost update, and in a
clinical or financial system it is a patient-safety or audit problem, not a
data-modelling curiosity.

## Options considered

1. **Pessimistic locking by default.** Correct, and disastrous for
   throughput and user experience: a clinician who opens a record and goes to
   lunch blocks the ward.
2. **Last-write-wins by default.** Fast, simple, and silently destroys data.
   The failure is invisible until someone notices a missing allergy note.
3. **Optimistic by default, conflict always surfaced**, with pessimistic
   locking and merge resolution available and explicit.

## Decision

Option 3.

- **Optimistic concurrency by default**, using the provider's native token:
  `rowversion` on SQL Server, `xmin` on PostgreSQL, an ETag elsewhere. The
  token is opaque — callers round-trip it and never interpret it.
- **Conflicts always surface** as `EDPF-DATA-3001` (HTTP 409) carrying the
  current version token, so the caller can re-read, merge and retry.
- **Silent last-write-wins is never the default.** It is available as an
  explicit `ConcurrencyStrategy`, which is a documented decision about data
  that can safely be overwritten — a cache warm-up timestamp, not a clinical
  observation.
- **Merge** delegates to a registered `IConflictResolver<T>`. A resolver that
  cannot merge safely **must** fail rather than guess.
- **Pessimistic locking** is available and explicit, for the short critical
  sections that genuinely need it.

Related and equally load-bearing: **tenant scoping is enforced at the
repository level, not by the caller.** A query against an
`ITenantScopedEntity` without a resolved tenant context is refused — it does
not quietly return everything. The adversarial suite verifies this through
ten routes: no context, empty specification, filtering `TenantId` directly,
OR-ing the predicate, projection, sort, encrypted-field filter, forged keyset
cursor, soft-delete escape, and per-tenant binding.

**Soft delete** filters automatically; including deleted rows requires an
explicit call that demands a written audit reason.

## Consequences

- Positive: a lost update is impossible to produce accidentally. The default
  is loud.
- Negative: callers must handle 409 — which is the point; the alternative is
  handling a support ticket six months later.
- Accepted risk: high-contention rows produce conflict churn. Measured by the
  conflict-rate gauge (Phase 05 metrics) and addressed per-entity with merge
  resolvers.

## Revisit trigger

The conflict-rate gauge shows a workload where optimistic concurrency is
pathologically wrong, warranting pessimistic default for that entity.
