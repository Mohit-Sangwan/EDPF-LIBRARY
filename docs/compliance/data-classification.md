# Data classification catalog

Phase 00 deliverable 4. The scheme is machine-discoverable in code via
`[DataClassification]` (`Edpf.Abstractions.Primitives`) — tagging is what
makes the Phase 23 classifier and Phase 22 DSAR tooling feasible, and what
rule EDPF0005 (never log a classified member) enforces against.

| Level | Definition | At rest | In transit | In logs | In memory | In exports |
|---|---|---|---|---|---|---|
| **Public** | Freely disclosable | — | TLS | Allowed | — | Allowed |
| **Internal** | Business data, not for external disclosure | Provider TDE | TLS | Allowed | — | Watermarked |
| **Confidential** | Commercially sensitive | Encrypted (tenant DEK) | TLS 1.2+ | **Never** | Minimize copies | Approved + logged |
| **PII** | Identifies a person (GDPR/DPDP) | Encrypted (tenant DEK) | TLS 1.2+ | **Never** — token only | Zero after use | DSAR pipeline only |
| **PHI** | Health information (HIPAA §164) | **Field-level, per-subject DEK** (ADR-006) | TLS 1.2+ | **Never** — token only | `KeyHandle` zeroed on dispose | De-identified per PS3.15 / Safe Harbor |
| **PCI** | Payment card data | **Tokenized, never stored raw** | TLS 1.2+ | **Never** | Never materialized | Never |

Working rules:

- Every new entity field is classified at introduction; unclassified defaults
  to Confidential handling until reviewed (fail closed).
- Subject references in audit records, domain events and logs are
  **pseudonymous tokens** (HMAC, tenant-salted, itself erasable — ADR-006).
- Tenant ids and correlation ids are Internal, not PII: they are opaque GUIDs
  and are required for operability.
- The walking skeleton demonstrates the PHI row of this table end-to-end:
  `Patient.MedicalRecordNumber` is PHI-tagged, ciphertext at rest (gate
  demonstration 5), tombstoned after erasure (demonstration 6).
