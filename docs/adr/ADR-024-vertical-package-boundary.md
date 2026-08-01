# ADR-024 — Domain verticals ship as optional packages, never in the core

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 24b — Healthcare Vertical Package
- **Supersedes:** the clinical operations placed on the framework surface by the originating specification
- **Related:** [ADR-001](ADR-001-data-core-strategy.md) (a single hardened core), [ADR-011](ADR-011-repository-topology.md) (package topology), [ADR-023](ADR-023-integrate-do-not-build.md) (the same instinct applied to standards)

## Context

The originating specification placed `SavePatient()`, `SaveEncounter()`,
`SaveLabOrder()` and similar operations directly on the framework surface.

That is a layering violation, and the master document names it as one: *"a
framework serving ERP, banking, and telecom cannot expose `SavePatient()`."*
The consequences are not stylistic:

1. **The commercial premise collapses.** EDPF's value is one hardened data
   platform reused across healthcare, ERP, banking and government. A core with
   `SavePatient()` on it is a healthcare product with delusions of generality.
2. **Every non-healthcare adopter pays a clinical tax.** They compile,
   version, audit and security-review clinical concepts they will never call.
3. **The core's extension model goes untested.** If clinical needs are met by
   editing the core, nobody discovers that the extension points are inadequate
   — the evidence is suppressed by the very shortcut that makes it convenient.

Healthcare is the design-partner domain, which makes the pressure constant
rather than occasional: for any clinical requirement, the fastest available fix
will always be to reach into the core.

## Decision

**Domain content lives in optional vertical packages that consume only the
core's public surface. The core stays domain-neutral, and this is enforced by
test rather than by convention.**

Concretely:

1. Verticals live under `verticals/`, are separately packaged, and are never a
   dependency of any `src/` project. The arrow points one way.
2. A vertical may reference only the public API of core packages. No
   `InternalsVisibleTo`, no shared source, no `link`ed files. **If a vertical
   cannot be built from the public surface, that is a defect in the core's
   extension model, and the fix is to improve the extension point — never to
   grant the vertical private access.**
3. The core carries no clinical vocabulary in its code. Comments and
   documentation may cite clinical examples, because "a medication
   administration time crossing a DST boundary" explains *why* a rule exists
   and losing that explanation costs more than the purity gains.
4. A vertical declares what its data *is* (via `[DataClassification]`) and
   inherits encryption, redaction, audit, export control and tenant isolation
   from the core. **A vertical that implements its own encryption is evidence
   the core failed.**

### Enforcement

[`CoreNeutralityTests`](../../tests/Edpf.ArchitectureTests/CoreNeutralityTests.cs)
makes the shortcut fail:

| Test | What it prevents |
| --- | --- |
| `CoreAssemblies_ContainNoClinicalTerminology` | Clinical vocabulary re-entering the core (13 terms, word-boundary matched, across 11 core assemblies) |
| `CoreAssemblies_DoNotReferenceAnyVerticalPackage` | A core package taking a dependency on a vertical, which would make the optional package mandatory |
| `VerticalPackage_BuildsOnThePublicSurfaceAlone` | `InternalsVisibleTo` granting a vertical private access |

`Edpf.Healthcare.Domain.csproj` is itself part of the evidence — what it does
*not* contain is the point: no internal reference, no shared source, no
`InternalsVisibleTo`.

## Consequences

### Accepted costs

- **The core cannot be tuned for clinical convenience.** A clinical need that
  the public surface cannot express becomes a core extension-point change with
  its own review, rather than a quick edit. This is slower, and it is the
  intended trade.
- **Some duplication across verticals is tolerated.** Two verticals may each
  build something similar before the shared shape is understood. Promoting to
  the core prematurely is the worse error, because it is the one that is hard
  to reverse.
- **The terminology test needs maintenance.** New clinical vocabulary must be
  added to the list, and the list will never be complete. It is a ratchet, not
  a proof.

### What this does not claim

The terminology test greps source text. It catches the realistic failure — a
developer adding `SavePatient()` to a core repository — and does not catch a
determined author who names the same concept `SaveSubjectRecord()`. Semantic
neutrality is not machine-checkable; this test raises the cost of the accident,
not of the intent.

## Revisit triggers

Per the master document's ADR discipline, revisit this decision if:

- **A second vertical is built and cannot be expressed on the public surface.**
  One vertical struggling is a gap in that vertical's design; two independently
  hitting the same wall is a gap in the core's extension model.
- **The neutrality test is suppressed or has exceptions added twice.** Two
  exceptions is a pattern, and the pattern means the boundary is in the wrong
  place.
- **A vertical implements its own encryption, audit or tenant filtering.** That
  is the core's extension model failing in the specific way this ADR exists to
  detect.
