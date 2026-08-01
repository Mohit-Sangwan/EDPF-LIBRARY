# ADR-021: Zero-downtime migration discipline

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Squad A lead

## Context

Schema evolution was absent from the original specification despite this
being a *data* platform — the difference between a framework usable in
production and one usable only for greenfield demos. Hospitals do not accept
maintenance windows for a framework upgrade.

## Options considered

1. **Migrations take a maintenance window.** Honest, and unsellable to the
   target market.
2. **Best-effort online migrations** without a discipline. Works until a
   rollback lands on a schema the previous version cannot read, at which point
   the outage is worse than the window would have been.
3. **Expand → migrate → contract, mandatory for every breaking change.**

## Decision

Option 3.

- **Expand** adds the new shape alongside the old: nullable columns, new
  tables, new indexes. Always safe, always deployable alone.
- **Migrate** backfills or moves data while both shapes are live.
- **Contract** removes the old shape, and is permitted only once every
  deployed version reads the new one — at least one release later. This is
  what makes rollback safe: a reverted application never meets a schema that
  cannot serve it.

A migration that genuinely requires downtime declares `RequiresDowntime` and
must be approved. Declaring it forces the author to think and the reviewer to
see.

**Migration locking** is mandatory. Ten pods starting simultaneously in a
rolling deployment must produce exactly one migration execution; this is a
common and destructive Kubernetes failure, not a theoretical one. The lease
expires, so a crashed migrator does not block the estate forever, and it
renews, so a slow migration is not mistaken for a crashed one.

**Drift detection fails readiness, not liveness.** A drifted instance is
removed from load balancing rather than crash-looping — crash-looping turns
one misconfigured pod into an outage and destroys the diagnostic evidence.

**Pre-flight planning** reports what would run, with duration estimates and
blocking-lock warnings, before anything changes. An operator needs that
warning before, not after.

## Consequences

- Positive: rollback is genuinely safe, which is what makes continuous
  deployment possible against a clinical database.
- Negative: a breaking change takes three releases instead of one. That is
  the price of not taking an outage, and it is the right trade.
- Accepted risk: consumers may write breaking migrations anyway. Mitigated by
  a migration analyzer that flags breaking operations at authoring time and
  requires an explicit acknowledgement attribute (Phase 11 §⑧).

## Revisit trigger

A provider appears whose DDL cannot be made online for the expand phase,
requiring a documented per-provider exception rather than a silent one.
