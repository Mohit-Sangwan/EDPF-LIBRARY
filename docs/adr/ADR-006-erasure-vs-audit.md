# ADR-006: Erasure vs. immutable audit — crypto-shredding

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Security Architect, Compliance Officer

## Context

Three obligations conflict head-on (Gap #18, severity C): GDPR right to
erasure, tamper-evident immutable audit, and clinical retention law. Deleting
audit rows breaks the chain; keeping identifiable data violates erasure;
erasing clinical records violates retention.

## Options considered

1. **Physical deletion with chain rebuild.** Rebuilding the hash chain after
   deletion is indistinguishable from tampering — it destroys the audit
   property it claims to preserve.
2. **Anonymization by field-scrubbing.** Update-in-place across every table
   and backup is unprovable and misses derived copies.
3. **Crypto-shredding.** Subject data is encrypted under a per-subject DEK;
   erasure destroys the DEK. Audit records carry pseudonymous subject tokens
   (HMAC under a tenant salt that is itself a destroyable key), never raw
   identifiers, so the hash chain survives erasure intact. Legal-hold and
   clinical-retention overrides are explicit, logged, and time-bounded.

## Decision

Option 3. Binding invariants, all proven in running code (Spike-C, now the
walking skeleton's component and gate tests):

- A destroyed key yields a **tombstone**, never an exception that leaks
  whether data existed.
- Audit records and domain events carry **tokens only** (BRL-006); payload
  snapshots are stored as ciphertext under the subject DEK.
- The erasure itself is audited, and that record survives the erasure it
  documents.
- Chain verification passes after shredding (`Demo6`,
  `Verify_AfterSubjectErasure_ChainStillValid`).

## Consequences

- Positive: erasure is provable (key destruction is a single auditable
  fact); audit and retention obligations survive simultaneously.
- Negative: per-subject key management at scale — key cache design is a
  Phase 20 deliverable with a Z.18 benchmark (`key resolution with/without
  cache`).
- Accepted risk: encrypted backups remain decryptable until their keys age
  out — retention policy for wrapped keys mirrors backup retention.

## Revisit trigger

A regulator or DPO of a design partner rejects crypto-shredding as
insufficient erasure; or key-resolution overhead exceeds its p99 budget at
Phase 31.
