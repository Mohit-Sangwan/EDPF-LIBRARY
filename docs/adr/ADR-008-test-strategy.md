# ADR-008: Test strategy — tiered matrix + one conformance suite

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, QA lead

## Context

13 databases × 5 TFMs × N features is combinatorially impossible to test
exhaustively (Gap #28). Untiered, the matrix either bankrupts CI or silently
shrinks to "whatever ran last".

## Options considered

1. **Full matrix on every commit.** Hours-long commit gate; Z.13's 15-minute
   budget is unmeetable; developers route around slow CI.
2. **Primary-provider testing only, others best-effort.** "Supported" becomes
   a marketing word; provider regressions ship.
3. **Tiered matrix + one identical conformance suite.**
   - **Tier A** (SQL Server, PostgreSQL): every commit, all TFMs.
   - **Tier B** (Oracle, MySQL, SQLite, MongoDB, Redis): nightly, two TFMs.
   - **Tier C** (Db2, HANA, Cosmos, Elastic, MariaDB): weekly + pre-release.
   - Every provider must pass the **same conformance suite 100%** — that
     suite is the definition of "Supported" (Z.12); anything less is
     Experimental or Preview.

## Decision

Option 3, plus the mandatory test types: unit (line ≥ 80%, branch ≥ 70%,
mutation ≥ 60% on core), integration via Testcontainers only, contract
(Pact), load (NBomber/k6), property-based, fuzz on every parser, adversarial
isolation, accessibility (WCAG 2.2 AA) for reference UIs.

Already standing in this repository: unit + architecture suites on every
build; the walking-skeleton gate suite runs identically on both Tier A
providers via Testcontainers; the ten conformance categories are pinned in
`Edpf.ConformanceTests` so the bar cannot drift before Wave 2 fills it.

## Consequences

- Positive: commit gate stays inside budget; "Supported" is falsifiable.
- Negative: Tier B/C regressions surface up to a day/week late — accepted,
  bounded by pre-release full runs.
- Accepted risk: mutation testing cost on core assemblies; scheduled
  nightly, not per-commit.

## Revisit trigger

A Tier B/C provider ships a regression that a Tier A run would have caught —
re-tier that provider; or commit-gate duration breaches 15 minutes
(parallelise or re-tier, never lower the bar).
