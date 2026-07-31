# ADR-004: Multi-tenancy — pluggable isolation, per-tenant keys

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Security Architect

## Context

Multi-tenancy strategy was entirely unstated in the original spec (Gap #6,
severity C) despite "millions of users" goals. The isolation model decides
schema design, key custody, connection routing, and the entire threat model's
first boundary.

## Options considered

1. **Shared schema + discriminator only.** Cheapest to operate; isolation
   rests entirely on the query layer; some regulated customers refuse it.
2. **Database-per-tenant only.** Strongest isolation; operationally heavy at
   thousands of tenants; makes small-tenant economics unviable.
3. **Pluggable, three modes:** shared-schema discriminator (default),
   schema-per-tenant, database-per-tenant — chosen per tenant at
   provisioning, enforced identically by the framework.

## Decision

Option 3, with three binding rules regardless of mode:

- **Tenant resolution is a first-class pipeline stage** (ADR-012 stage 2),
  before authentication and always before any data access.
- **`TenantId` leads every clustered index** (Z.2) so isolation is free, not
  costly; the tenant filter is enforced in code (global query filter +
  rule EDPF0009), never by caller discipline.
- **Per-tenant data-encryption keys wrapped by a tenant KEK** — a tenant's
  data is cryptographically, not just logically, partitioned.
- Cross-tenant access answers **404, never 403** (EDPF-AUTHZ-2102): existence
  is not disclosed across the boundary.

The walking skeleton implements shared-schema mode end-to-end, including the
adversarial case (gate demonstration 2).

## Consequences

- Positive: one isolation contract, three price points; the adversarial
  isolation suite (Z.19) tests all modes identically.
- Negative: three modes to certify per provider; bounded by the conformance
  suite's cross-tenant category.
- Accepted risk: shared-schema noisy-neighbor effects — addressed by
  Phase 30 per-tenant quotas, not by weakening isolation.

## Revisit trigger

An isolation-suite finding that any mode's boundary depends on caller
discipline; or a design partner demands a fourth mode (escalates via the
stopping rule I.4).
