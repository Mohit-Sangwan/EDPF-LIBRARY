# Wave 2 — Data Access Core (Phases 06–11)

**Status:** Contracts and safety-critical paths complete; engine-bound work
carried forward · **Gate:** G2 (Data Core) · **Squad:** A

> **Purpose:** the heart of the framework. Everything here is governed by
> ADR-001 (wrap, don't build) and ADR-003 (no cross-store 2PC).

## Constraining ADRs

ADR-001, ADR-003, ADR-005, ADR-008, ADR-010.
**Produces** ADR-016 (capability negotiation), ADR-017 (connection routing &
resilience), ADR-018 (query construction safety), ADR-019 (idempotency
contract), ADR-020 (concurrency default), ADR-021 (zero-downtime migration).

## What this increment delivers

| Phase | Delivered | Location |
|---|---|---|
| **06** | `IDataProvider`, `IProviderCapabilities`, `ISqlDialect`, `ITypeMapper`, `IProviderRegistry`; SQL Server + PostgreSQL dialects | `src/Edpf.Abstractions/Data/`, `src/Edpf.Data/Dialects/` |
| **07** | `IConnectionRouter`, `IReplicaSelector`, `ReadWriteIntent`, `SessionConsistencyToken`, `DatabaseEndpoint` | `src/Edpf.Abstractions/Data/IConnectionRouter.cs` |
| **08** | `ISpecification<T>`, closed `FilterOperator`, filter tree, `FilterCompiler`, `QueryCompiler`, offset **and** keyset pagination | `src/Edpf.Abstractions/Query/`, `src/Edpf.Data/Query/` |
| **09** | `ISaga<TState>`, `ISagaStep<TState>`, `ISagaCoordinator`, `SagaCoordinator` with compensation-failure escalation | `src/Edpf.Abstractions/Consistency/`, `src/Edpf.Data/Consistency/` |
| **10** | `IConcurrencyToken`, `ConcurrencyStrategy`, `IConflictResolver<T>`, `ISoftDeletable`; repository-level tenant enforcement | `src/Edpf.Abstractions/Data/IConcurrencyToken.cs`, `src/Edpf.Data/Query/QueryCompiler.cs` |
| **11** | `IMigrationRunner`, `IMigration`, `MigrationPhase`, `IMigrationLock`/`IMigrationLease`, `ISchemaDriftDetector` | `src/Edpf.Abstractions/Data/IMigrationRunner.cs` |

## The two properties that matter

**Injection is unrepresentable, not merely prevented.** Identifiers come from
metadata and are *rejected* if illegal rather than escaped; operators come
from a closed enum; values are always parameters. The corpus suite therefore
asserts something stronger than "no injection succeeded": a hostile value and
a benign one produce **byte-identical SQL**.

**The tenant predicate is unavoidable.** It is emitted first,
unconditionally, before any caller-supplied filter, and an unresolved tenant
is refused rather than treated as "all tenants". Ten adversarial routes are
verified.

## Verification

446 automated tests green. See [09-test-plan.md](09-test-plan.md) for the
mapping from each Phase 06–11 verification requirement to its test, and
[14-completion-report.md](14-completion-report.md) for what is explicitly
carried forward to Gate G2.
