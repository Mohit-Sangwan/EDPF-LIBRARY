# ADR-005: Schema migration & evolution

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Data squad lead

## Context

Consumers must evolve schema across up to thirteen engines (Gap #7, severity
C) with zero-downtime expectations in hospital deployments. No single
migration tool covers relational + document stores + the licensed edge
engines.

## Options considered

1. **One bespoke migration engine for all thirteen stores.** Principle 0
   failure: enormous build, no differentiation.
2. **EF Core Migrations everywhere.** Covers the EF-supported relational set
   well; does not exist for document versioning and is weak on
   Oracle/Db2/HANA DBA-workflow realities.
3. **Composite:** EF Core Migrations for supported relational engines; a
   versioned SQL-script runner (checksummed, ordered, transactional where the
   engine allows) for Oracle/Db2/HANA edge cases; document-store schema
   versioning via lazy upcasting on read. **Expand–migrate–contract
   discipline is mandatory** for every breaking change.

## Decision

Option 3. Every migration is forward-only in production; contract steps ship
at least one release after their expand step; migration runners are
concurrent-startup-safe (verified in the provider conformance suite, Z.12).

The walking skeleton deliberately uses `EnsureCreated` + seed in Development
only — recorded as [TDL-0001](../tdl/TDL-0001-skeleton-ensurecreated.md); the
real machinery is Phase 11's deliverable and this ADR is its contract.

## Consequences

- Positive: each store gets the evolution mechanism its ecosystem actually
  supports; DBA review workflows survive for the engines that demand them.
- Negative: two mechanisms to operate (EF + script runner); unified behind
  `IMigrationRunner` in Phase 11.
- Accepted risk: lazy upcasting spreads read-path cost — bounded by
  measuring upcast rates and scheduling background rewrites.

## Revisit trigger

Phase 11 implementation shows the script runner converging on a rebuild of
an existing tool (e.g. DbUp/Flyway class) — switch to wrapping that tool per
Principle 0.
