# Phase 01 — Completion report

**Phase:** p01 · **Date:** 2026-08-01 · **Squad:** All hands

## What was built

Repository topology per Z.1; build system enforcing ADR-002's five TFMs with
warnings-as-errors, nullable, deterministic builds and central package
management; the Z.4 analyzer set; the shared kernel; the polyfill boundary;
diagnostics contracts; CI (build → analyzers → unit → architecture → coverage
→ SBOM → secret scan → SAST/CodeQL); and the architecture-test suite that
makes the rules executable.

Produced [ADR-011](../../adr/ADR-011-repository-topology.md).

## Verification

| Exit criterion | Evidence |
|---|---|
| Builds clean on all five TFMs, warnings as errors | `dotnet build Edpf.slnx -c Release` → Build succeeded (net472, net48, net6.0, net8.0, net10.0) |
| Architecture tests pass and fail correctly when violated | 12/12 green; negative verification recorded in [09-test-plan.md](09-test-plan.md#negative-verification) |
| Unit tests green | 59/59 |
| Public API tracked | 249-entry baseline; RS0016 escalated to error |
| Coding standards mechanical | `.editorconfig` severities; `TreatWarningsAsErrors` |

## Decisions taken during implementation

Several analyzer rules are deliberately disabled repo-wide, each with the
reason recorded in `.editorconfig` rather than suppressed at call sites:

- **CA1510–CA1513** (throw-helpers): the helpers do not exist on Tier 3 TFMs
  and `#if` is confined to `Edpf.Compatibility` — this is ADR-002's cost,
  paid visibly.
- **CA1819** (array properties): defensive copies on the per-field crypto hot
  path would breach the Z.18 allocation budget; ownership is documented on
  each property and enforced by review (Z.6).
- **CA1000** (static members on generic types): required by the
  strongly-typed id pattern; the alternative erases the type brand.
- **CA2225** (operator alternates): named alternates exist
  (`FromValue`/`FromError`); the analyzer's heuristic does not recognise them
  for generic conversions.

## Follow-ups into later phases

- Dedicated Roslyn analyzers for EDPF0001–EDPF0015 (Phase 33 tooling); until
  then the rules run as architecture/source tests at identical severity.
- `PublicAPI.Shipped.txt` promotion at Gate G1.
- Mutation testing (≥ 60 % core) wired nightly from Phase 32.

**Phase 01 exit criteria: MET.** Proceed to Phase 02.
