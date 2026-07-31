# ADR-011: Repository & package topology

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Platform squad lead

## Context

Phase 01 must fix how the codebase is organized before 36 later phases
depend on it: one repository or many, and how optional/commercially-licensed
providers stay out of the core package graph (consumes ADR-002, ADR-008,
ADR-009).

## Options considered

1. **Multi-repo (core, providers, verticals separate).** Independent release
   cadence; but cross-cutting changes (an `IDataProvider` evolution) become
   multi-repo choreography, and the conformance suite drifts from providers.
2. **Monorepo, single package.** One artifact; forces every consumer to
   carry every provider's dependency graph — violates ADR-009's isolation.
3. **Monorepo, one project per package** (Z.1): `src/` pillars,
   `providers/` isolated (depend on `src/` only, never on each other),
   `verticals/`, one version for every package in a release (Z.14), central
   package management, restricted-license providers as optional packages.

## Decision

Option 3, exactly as laid out in Appendix Z.1 — this repository implements
it. Enforced mechanically: `Edpf.Abstractions` has zero package references
(architecture test), `samples/` are never referenced by `src/`, analyzers
and TFMs flow from one `Directory.Build.props`, versions from one
`Directory.Packages.props`.

## Consequences

- Positive: atomic cross-cutting changes; the conformance suite and every
  provider live in the same commit; one CI, one rulebook.
- Negative: repo size grows with providers; bounded by build tiering
  (ADR-008) so CI cost does not scale linearly with provider count.
- Accepted risk: monorepo access control is coarse; CODEOWNERS provides
  path-level review ownership.

## Revisit trigger

A provider needs an out-of-band release cadence (e.g. an urgent driver CVE
while core is mid-release) more than once per quarter — revisit splitting
`providers/` packaging, not the source tree.
