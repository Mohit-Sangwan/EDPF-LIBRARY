# Phase 01 — Foundations: Solution, Build, and Shared Kernel

**Status:** Complete · **Squad:** All hands · **Depends on:** G0 part 1

## Purpose

Establish the repository, build system, multi-targeting mechanics, coding
standards, and the tiny shared kernel every later assembly depends on. All 36
subsequent phases depend on this, so it must be correct rather than fast.

## Constraining ADRs

ADR-002 (TFM tiers) · ADR-008 (test matrix) · ADR-009 (package/licence
boundaries). **Produces** [ADR-011](../../adr/ADR-011-repository-topology.md).

## Scope

**In:** solution layout, `Directory.Build.props`, TFM conditionals, analyzers,
CI skeleton, shared-kernel primitives.
**Out:** data access, security implementation, business capability.

## Deliverables

| Deliverable | Location |
|---|---|
| Solution structure per Z.1 | repository root |
| Build configuration (5 TFMs, warnings-as-errors, deterministic) | [`Directory.Build.props`](../../../Directory.Build.props) |
| Central package management | [`Directory.Packages.props`](../../../Directory.Packages.props) |
| Analyzer rule set (Z.4) | [`.editorconfig`](../../../.editorconfig) |
| Shared kernel | `src/Edpf.Core`, `src/Edpf.Abstractions` |
| Polyfill boundary | `src/Edpf.Compatibility` |
| Diagnostics contracts | `src/Edpf.Diagnostics` |
| Public API surface tracking | `src/Edpf.Abstractions/PublicAPI.*.txt` |
| CI skeleton | [`.github/workflows/ci.yml`](../../../.github/workflows/ci.yml), [`codeql.yml`](../../../.github/workflows/codeql.yml) |
| Architecture tests | `tests/Edpf.ArchitectureTests` |

## Shared kernel contents (deliberately small)

`Result` / `Result<T>` · `Error` + `ErrorCategory` + `ErrorCodes` (the §10.2
taxonomy seed) · `IClock` over the `EdpfTime` polyfill · `EntityId<T>` ·
`PagedResult<T>` / `PageRequest` · `ITenantContext` / `ITenantResolver` /
`ITenantContextAccessor` (the seam Phase 12 fills — declared now so no layer
is written tenant-blind) · `ICorrelationContext` + accessor · `Guard` ·
`DataClassification` attributes.

Additions require Chief Architect approval and written justification
(Phase 01 §⑧: the kernel must not become a junk drawer).

## Exit criteria — status

- [x] `dotnet build` succeeds on all five TFMs with warnings as errors.
- [x] Architecture tests pass **and fail correctly** when violated — see
      [09-test-plan.md](09-test-plan.md#negative-verification).
- [x] CI defined end-to-end: build → analyzers → unit → architecture →
      coverage → SBOM → secret scan → SAST.
- [x] Coding standards enforced mechanically (analyzers as errors), not by
      review convention.
