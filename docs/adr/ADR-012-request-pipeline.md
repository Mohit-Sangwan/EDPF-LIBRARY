# ADR-012: Request pipeline composition

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, all squad leads

## Context

Every cross-cutting concern lands somewhere in the request path. Without a
fixed order, the classic failures appear: audit before authorization (audits
denied requests' payloads), tenant resolution after data access (tenant-blind
queries), validation after side effects. Phase 02 must fix the order every
subsequent phase plugs into.

## Options considered

1. **Per-host, per-team ordering.** Each host re-derives the order; the
   failure modes above appear one team at a time.
2. **Framework-magic implicit ordering** (attributes discovered at runtime).
   Order exists but is invisible; debugging requires reading the resolver.
3. **One canonical, explicit, test-enforced order:**

   ```text
   Request
     → Correlation ID assignment
     → Tenant resolution
     → Authentication
     → Authorization
     → Validation
     → Idempotency check
     → Handler
     → Transaction / outbox commit
     → Audit emission
     → Telemetry emission
     → Response (RFC 9457 on failure)
   ```

## Decision

Option 3. The order is code (`PipelineStages.CanonicalOrder`), the
composition root records what it actually composed
(`PipelineStages.ComposedOrder`), and tests assert both: the canonical list
matches this ADR verbatim (architecture test) and the booted skeleton
composes exactly that order (gate test). Reordering the pipeline therefore
requires superseding this ADR — the build fails otherwise.

Two consequences of the order are load-bearing:

- **Tenant resolution precedes authentication**: identity is evaluated
  inside a tenant scope, and an unknown tenant 404s before credentials are
  examined.
- **Audit rides the transaction** (stages 8–9): a create and its audit
  record commit or roll back together (BRL-005).

## Consequences

- Positive: the failure classes this order prevents are structurally
  impossible, not review-caught.
- Negative: hosts with exotic transports (message consumers, devices) must
  map their pipeline onto the same stages — deliberate, so Phase 26
  consumers inherit the same guarantees.
- Accepted risk: none material; the order encodes settled security practice.

## Revisit trigger

A host type appears whose transport cannot express a stage (e.g. tenant
resolution for unauthenticated public endpoints) — the exemption list is
extended by ADR supersession, never ad hoc.
