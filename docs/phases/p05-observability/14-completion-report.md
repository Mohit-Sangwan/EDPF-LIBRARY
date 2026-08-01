# Wave 1 — Completion report (Gate G1: Foundation)

**Phases:** p03, p04, p05 · **Date:** 2026-08-01 · **Squad:** C

## What was built

The three cross-cutting systems every other phase consumes: configuration and
secret custody, the composition root, and observability with redaction.
Produced ADR-013, ADR-014 and ADR-015.

## Verification

**164 automated tests green** — 137 unit, 14 architecture, 12 walking-skeleton
component, 1 conformance contract. Solution builds clean on all five TFMs
with warnings as errors.

**24/24 live gate checks passed** (G0's nineteen plus five new G1 checks)
against a real SQL Server instance:

| G1 check | Result |
|---|---|
| Composition root validated at boot — captive sweep + `ValidateOnBuild`/`ValidateScopes` | Pass |
| Liveness independent of dependencies | Pass — HTTP 200 |
| Readiness reflects dependencies | Pass — HTTP 200 |
| Correlation intact across 25 consecutive requests | Pass — 0 mismatched |
| No raw identifiers in audit subject tokens | Pass — 0 GUID-shaped tokens |

The skeleton now composes through `AddEdpfCore()`, layers secret stores
through the chain, and enforces captive-dependency detection before the
container is built — so G1 is demonstrated by running code, not asserted.

## Two defects the tests caught

**1. `AddEdpfCore` was not idempotent.** Each call constructed a fresh
builder, so its module-registration guard never saw prior state and every
registration was silently duplicated. A defensive second call from a feature
module would have produced two `IClock` singletons. Fixed by registering the
builder itself, so repeated calls share one module set. Caught by
`AddEdpfCore_CalledTwice_IsIdempotent`.

**2. Exception messages leaked PHI.** The redactor sanitised exception text
for log-injection characters but still emitted it, and a domain exception had
interpolated a medical record number into its message. Fixed by surrendering
exception messages by default and keeping only the type, with an opt-in
registry for types whose messages are contractually code-only. Caught by
adversarial route 3, on its first run.

Both are the intended economics of the walking-skeleton and adversarial-suite
approach: found in a day, at a cost of an hour, instead of in Wave 4 or in
production.

## Decisions taken during implementation

- **CA2000 disabled for secret stores only**, path-scoped in `.editorconfig`
  with the reason recorded: the analyzer cannot model ownership transfer of a
  disposable through `Result<T>`, and the contract ("the caller disposes") is
  documented on `ISecretStore`. Everything outside that folder still fails on
  a genuine undisposed resource.
- **`Microsoft.Extensions.*` pinned to the 9.0.x line**, which targets
  netstandard2.0 and therefore serves all five TFMs from one version — no
  per-TFM divergence in the platform-core dependency graph.
- Public API baseline grew to 287 entries; every addition required an
  explicit `PublicAPI.Unshipped.txt` edit, exactly as EDPF0013 intends.

## Explicitly not done, and why

Stated plainly rather than left to look complete:

- **Cloud secret backends** (Key Vault, AWS Secrets Manager, Vault) — the
  contract, conformance suite, chain and rotation coordinator are built; the
  three adapters are not. They cannot be integration-tested without live
  cloud credentials, and Z.12 forbids claiming an untested capability.
  Rationale and plan: [p03/13-extension-points.md](../p03-configuration-secrets/13-extension-points.md).
- **Legacy-host scoping proven in situ** — the WebForms/WinForms/WPF adapters
  exist and are unit-covered, but no Tier 3 host application exists yet to
  prove them. Closes with reference applications 6–7 at Phase 35.
- **Logging overhead and container build-time benchmarks** — both belong to
  the Phase 31 baseline; the graph and load are currently too small for a
  meaningful number, and an unmeasured performance claim is exactly what
  Golden Rule 4 forbids.
- **Rotation under sustained load with zero failed requests** — the mechanism
  is proven at unit level including its failure path; the zero-downtime claim
  needs a load test and is therefore not made.

## Gate G1 (Foundation)

**PASSED on the criteria this increment covers.** The unmet exit criteria
above are carried forward with named owners and phases; none of them blocks
Wave 2, which depends on the contracts and the composition root, both of which
are complete and verified.
