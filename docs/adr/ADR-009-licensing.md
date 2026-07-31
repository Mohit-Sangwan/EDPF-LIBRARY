# ADR-009: Licensing — dual license, isolated restricted drivers

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Sponsor, Chief Architect, Legal

## Context

Third-party licensing is a ship-blocking legal risk (Gap #30): Oracle, SAP
HANA and Db2 client licenses are restrictive; Redis and Elastic changed
licenses upstream; and EDPF itself had no stated license — which defaults to
"all rights reserved" and blocks every adoption conversation.

## Options considered

1. **Pure OSS (MIT/Apache-2.0).** Maximizes adoption; surrenders the
   commercial model that funds the compliance/certification work enterprises
   buy EDPF for.
2. **Fully proprietary.** Protects revenue; kills community adoption and
   design-partner evaluation friction.
3. **Dual: source-available core + commercial enterprise license**, with
   restrictive third-party drivers isolated in optional provider packages so
   the core package graph ships license-clean.

## Decision

Option 3.

- The core (`src/`, Tier A providers, samples, docs) is source-available
  (BUSL-1.1-style terms, see `LICENSE.md`); production use beyond the grant
  requires the commercial license.
- Oracle/HANA/Db2 providers ship as **optional packages** under
  `providers/`, never referenced by the core graph (Z.1 rule: no project
  references across `providers/`); their client libraries are consumer-
  supplied where redistribution is not permitted.
- Redis and Elastic integrations are assessed against their current upstream
  licenses at the phase that builds them, recorded in the TDL.
- Every build produces an SBOM (CycloneDX in CI); a license-policy gate
  fails the build on a disallowed license entering the graph.

## Consequences

- Positive: evaluation is frictionless; the core graph is legally clean by
  construction; commercial model preserved.
- Negative: dual-license bookkeeping (CLA for external contributions —
  tracked as a program-readiness item, F.B).
- Accepted risk: BUSL-style terms deter some communities; mitigated by the
  change-date conversion typical of that license class.

## Revisit trigger

Legal review (pre-v0.9 packaging, Phase 34) amends terms; or a design
partner's procurement rejects the model.
