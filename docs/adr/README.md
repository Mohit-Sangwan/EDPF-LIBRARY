# Architecture Decision Records

The binding decisions of Phase 00–02 (Book 2, D2; Playbook Phases 00–02).
Every phase begins by restating which of these constrain it (Golden Rule 1).

**Change control (Z.21):** an ADR is never edited in place once accepted.
When a revisit trigger fires, a superseding ADR is written and the old one is
marked Superseded — superseded ADRs are never deleted; they record why the
current answer is the current answer.

| ADR | Title | Status |
|---|---|---|
| [ADR-001](ADR-001-data-core-strategy.md) | Data core strategy — wrap, don't build | Accepted |
| [ADR-002](ADR-002-multi-target-strategy.md) | Multi-target strategy — tiered TFM surface | Accepted |
| [ADR-003](ADR-003-consistency-model.md) | Consistency — outbox + saga + idempotency, no 2PC | Accepted |
| [ADR-004](ADR-004-multi-tenancy-model.md) | Multi-tenancy — pluggable isolation, per-tenant keys | Accepted |
| [ADR-005](ADR-005-schema-migration.md) | Schema migration & evolution | Accepted |
| [ADR-006](ADR-006-erasure-vs-audit.md) | Erasure vs. immutable audit — crypto-shredding | Accepted |
| [ADR-007](ADR-007-crypto-agility.md) | Crypto-agility — algorithm registry + versioned envelopes | Accepted |
| [ADR-008](ADR-008-test-strategy.md) | Test strategy — tiered matrix + conformance suite | Accepted |
| [ADR-009](ADR-009-licensing.md) | Licensing — dual license, isolated restricted drivers | Accepted |
| [ADR-010](ADR-010-data-residency.md) | Data residency — region-pinned routing | Accepted |
| [ADR-011](ADR-011-repository-topology.md) | Repository & package topology | Accepted |
| [ADR-012](ADR-012-request-pipeline.md) | Request pipeline composition | Accepted |
| [ADR-013](ADR-013-configuration-precedence.md) | Configuration precedence & reload semantics | Accepted |
| [ADR-014](ADR-014-composition-lifetime-policy.md) | Composition & lifetime policy | Accepted |
| [ADR-015](ADR-015-telemetry-redaction-policy.md) | Telemetry standard & redaction policy | Accepted |
| [ADR-016](ADR-016-capability-negotiation.md) | Capability negotiation | Accepted |
| [ADR-017](ADR-017-connection-routing-resilience.md) | Connection routing & resilience | Accepted |
| [ADR-018](ADR-018-query-construction-safety.md) | Query construction safety | Accepted |
| [ADR-019](ADR-019-idempotency-contract.md) | Idempotency contract | Accepted |
| [ADR-020](ADR-020-concurrency-default.md) | Concurrency default | Accepted |
| [ADR-021](ADR-021-zero-downtime-migration.md) | Zero-downtime migration discipline | Accepted |
| [ADR-022](ADR-022-audit-event-taxonomy.md) | Audit event taxonomy | Accepted |
| [ADR-023](ADR-023-integrate-do-not-build.md) | Integrate, do not build (FHIR, HL7 v2, DICOM) | Accepted |
| [ADR-024](ADR-024-vertical-package-boundary.md) | Vertical package boundary — domain content never enters the core | Accepted |

Template: [ADR-000-template.md](ADR-000-template.md).
Smaller decisions go to the [technical decision log](../tdl/README.md).
