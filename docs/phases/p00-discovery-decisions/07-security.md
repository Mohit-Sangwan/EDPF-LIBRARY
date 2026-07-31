# Phase 00 — Threat model (STRIDE)

Phase 00 deliverable 3. The primary output of this phase: every later phase
updates it when attack surface changes (PR checklist, Z.5.3).

## Assets

Tenant data (PHI/PII), key material, audit chain, tenant metadata,
credentials/tokens, configuration and secrets, the build/supply chain.

## Trust boundaries

1. Internet → API edge (untrusted callers).
2. Tenant A ↔ Tenant B (the boundary that defines the product).
3. Application → data stores and blob storage.
4. Application → key custody (KMS/HSM).
5. CI/CD → published artifacts (supply chain).

## STRIDE register

| # | Threat | Category | Boundary | Control | Where |
|---|---|---|---|---|---|
| T1 | Caller forges a tenant header to read another tenant's data | Spoofing / Info disclosure | 2 | Tenant resolution against the tenant store + structural query filter; cross-tenant answers 404 | ADR-004, ADR-012 · **proven** (gate demo 2) |
| T2 | Authenticated user of tenant A guesses tenant B's record id | Info disclosure | 2 | Global tenant filter makes the row nonexistent; no existence disclosure | EDPF-TEN-001 · **proven** |
| T3 | SQL/NoSQL injection through filters or projections | Tampering / Elevation | 1, 3 | Parameterization only; closed operator enum; field whitelist against metadata; no string-built queries (Z.3 rule 6) | ADR-030 path, Phase 08b; injection corpora Z.19 |
| T4 | Audit record altered or deleted to hide access | Repudiation | 3 | Per-tenant hash chain with canonical bytes; fork-safe append; scheduled verification | ADR-006 · **proven** (tamper + deletion tests) |
| T5 | Operator with database access reads PHI directly | Info disclosure | 3 | Field-level encryption under per-subject DEK; DB holds ciphertext only | ADR-006/007 · **proven** (gate demo 5) |
| T6 | Key compromise yields all tenants' data | Info disclosure | 4 | Key hierarchy master → tenant KEK → per-subject DEK; blast radius is one subject/tenant; zeroization on dispose | ADR-004/007 · **proven** |
| T7 | Erasure request cannot be honored, or honoring it breaks audit | Compliance failure | 3 | Crypto-shredding; audit holds tokens; chain verifies post-shred | ADR-006 · **proven** (gate demo 6) |
| T8 | Retry or network duplicate creates a duplicate clinical act | Tampering / Integrity | 1 | Idempotency keys; same key + different payload → 409 | ADR-003 · **proven** |
| T9 | Partial cross-store write leaves systems diverged | Integrity | 3 | Transactional outbox; no dual writes; at-least-once + idempotent consumers | ADR-003 · **proven** (gate demo 7) |
| T10 | PHI leaks into logs, traces, metrics or error bodies | Info disclosure | 1, 3 | Classification attributes + rule EDPF0005; RFC 9457 detail limits per code; tokens in logs | §10.2 · **proven** (gate demos 4, 8) |
| T11 | Error messages enumerate schema or valid values | Info disclosure | 1 | Error catalogue fixes what each code may disclose; filter errors never list alternatives | §10.2 |
| T12 | Break-glass access abused | Elevation | 2, 4 | Time-bounded grants, audited invocation and expiry, alerting | Phase 21 |
| T13 | Cross-region access violates residency law | Compliance failure | 3 | Region-pinned resolver refuses by default; auditable break-glass | ADR-010, Phase 12/22 |
| T14 | Credential theft / token replay | Spoofing | 1 | Short-lived tokens, audience+issuer+lifetime validation, MFA and step-up | Phase 21 · **partial** (JWT validation in skeleton) |
| T15 | Denial of service via unbounded queries or uploads | DoS | 1, 3 | Bounded page size (`PageRequest.MaxPageSize`), query cost estimator, rate limits, streaming with bounded memory | **partial** (paging bound proven) |
| T16 | Supply-chain compromise of a dependency | Tampering | 5 | SBOM per build, license + CVE gate, secret scanning, signed artifacts (SLSA L3) | ADR-009, Phase 34 · **partial** (SBOM + scans in CI) |
| T17 | Malicious file upload (malware, zip bomb, path traversal) | Tampering / DoS | 1, 3 | Magic-byte validation, size limits, virus scan hook, tenant-namespaced paths | Phase 14 |
| T18 | Algorithm becomes cryptographically broken during retention | Info disclosure | 3 | Self-describing envelopes; registry migration without data migration | ADR-007 · **proven** |

**"Proven"** means an automated test in this repository fails if the control
regresses. **"Partial"** means the mechanism exists at skeleton scale and is
completed by the named phase.

## Residual risks accepted at G0

- Tier 3 (net472/net48) hosts receive a reduced security surface (ADR-002);
  brownfield deployments must compensate at the host.
- Development-mode ephemeral keys (TDL-0003) are unsuitable for any shared
  environment — enforced by startup failure outside Development.
- Skeleton dispatch is a log entry, not a broker (TDL-0002); message-level
  authenticity is Phase 26.
