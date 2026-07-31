# ADR-010: Data residency — region-pinned routing

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Compliance Officer

## Context

GDPR and India's DPDP Act impose data-localization obligations (Gap #19).
Residency retrofitted after connection routing exists means auditing every
code path that ever touched a connection string.

## Options considered

1. **Deployment-level separation only** (one stack per region). Simple;
   fails the moment one tenant's users span regions or a global operator
   needs a single control plane.
2. **Application-code discipline** ("remember to check the region"). Not a
   control; unauditable.
3. **Region as a first-class routing dimension in tenant metadata.** The
   connection resolver refuses cross-region access by default; break-glass
   exists, is auditable, and is time-bounded.

## Decision

Option 3. `ITenantContext.Region` is part of the tenant contract from
Phase 01 (already on `TenantDescriptor` in this repository — no layer is
being written region-blind). Phase 12 implements the region-pinned
`IConnectionRouter`; Phase 22 adds `IDataResidencyEnforcer` checks and the
EDPF-CMP-6002 refusal (403, detail limited to regions). Break-glass grants
are logged to the audit chain with an expiry.

## Consequences

- Positive: residency is enforceable and evidencable — the refusal is a
  control in the compliance matrix, not a policy document.
- Negative: cross-region features (global search, analytics) must be
  designed around residency from the start — correct, if inconvenient.
- Accepted risk: region metadata quality; tenant provisioning validates
  region codes against the deployment topology.

## Revisit trigger

A jurisdiction imposes requirements routing cannot satisfy (e.g. hardware
attestation), escalating to deployment-level separation for that region.
