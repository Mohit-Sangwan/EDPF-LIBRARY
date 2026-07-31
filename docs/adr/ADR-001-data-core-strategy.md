# ADR-001: Data core strategy — wrap, don't build

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, squad leads

## Context

The original specification ("provider abstraction + command engine + query
builder + repository") describes a from-scratch micro-ORM. That is the most
expensive possible path, and it is not where EDPF differentiates: Principle 0
says EDPF's defensible product is **compliance, tenancy and audit — not data
access**. The choice reshapes roughly 20 of the phases downstream.

## Options considered

1. **Build a micro-ORM on raw ADO.NET.** Full control over every code path;
   no third-party coupling. Cost: years of engineering on a solved problem;
   EDPF would compete with EF Core's maintainers on their home ground and
   lose (Principle 0 test fails on both halves).
2. **Standardize on a single existing ORM with no abstraction.** Cheapest.
   Cost: consumers are hard-coupled to that ORM's API and lifecycle; the
   framework cannot swap hot paths to Dapper or native SDKs; contradicts the
   database-agnostic charter.
3. **Wrap: EF Core for relational modeling and migrations, Dapper for
   measured hot paths, native SDKs for NoSQL — all behind EDPF abstractions
   (`IRepository`, `ICommandExecutor`, `IDataProvider`).** Build only the
   unifying abstraction and the compliance/tenancy/audit behavior around it.

## Decision

Option 3. EF Core is the relational backbone; Dapper is admitted per hot path
only with a benchmark showing it wins (Z.9); NoSQL stores use their native
SDKs. Every third-party engine sits behind an EDPF abstraction so it can be
replaced (Principle 0). The walking skeleton already exercises this shape:
EF Core underneath, `IRepository<Patient, Guid>` on top.

## Consequences

- Positive: engineering lands on the differentiators; migrations, LINQ
  translation and provider quirks are outsourced to maintained libraries.
- Negative: abstraction tax on every call path — bounded by the Z.18
  benchmark budget (repository overhead vs. raw ADO.NET is published, not
  asserted).
- Accepted risk: EF Core major-version churn; mitigated by the abstraction
  seam and central package management.

## Revisit trigger

A Z.18 benchmark shows the wrapper exceeding its overhead budget on a Tier A
provider and profiling attributes it to the abstraction itself, not usage; or
EF Core licensing/support posture changes materially.
