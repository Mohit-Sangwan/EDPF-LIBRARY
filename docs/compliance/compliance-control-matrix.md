# Compliance-control matrix

Phase 00 deliverable 5.

> **Mandatory framing:** EDPF ships *controls that enable* compliance. A
> framework cannot make an application HIPAA- or GDPR-compliant; compliance
> is a property of the system, its processes, and its organization. Neither
> documentation nor marketing may claim otherwise (Golden Rule 5).

Control → clause mapping. "Implemented in" names the phase that delivers the
control; **WS** marks controls already demonstrated by the walking skeleton.

| Control ID | Control | Implemented in | HIPAA §164 | GDPR Art. | ISO 27001 Annex A | SOC 2 TSC |
|---|---|---|---|---|---|---|
| EDPF-AUD-001 | Tamper-evident audit chain (per-tenant hash chain, fork-safe append) | 19 · **WS** | 312(b) | 30 | A.8.15 | CC7.2 |
| EDPF-AUD-002 | Audit records carry subject tokens, never raw identifiers | 19 · **WS** | 514 | 5(1)(c) | A.8.11 | CC6.5 |
| EDPF-AUD-003 | Failed audit fails the audited operation (BRL-005) | 19 · **WS** | 312(b) | 32 | A.8.15 | CC7.2 |
| EDPF-SEC-001 | Field-level encryption of PHI under per-subject DEK | 20 · **WS** | 312(a)(2)(iv) | 32 | A.8.24 | CC6.1 |
| EDPF-SEC-002 | Envelope crypto-agility incl. PQC posture | 20 · **WS** | 312(e)(2)(ii) | 32 | A.8.24 | CC6.1 |
| EDPF-SEC-003 | Key custody hierarchy (master → tenant KEK → DEK), zeroization | 20 · **WS** | 312(a)(2)(iv) | 32 | A.8.24 | CC6.1 |
| EDPF-SEC-004 | Per-tenant encryption keys | 12, 20 · **WS** | 312(a)(2)(iv) | 32 | A.8.24 | CC6.1 |
| EDPF-TEN-001 | Structural tenant isolation; cross-tenant reads answer 404 | 12 · **WS** | 312(a)(1) | 32 | A.8.3 | CC6.3 |
| EDPF-TEN-002 | Tenant resolution precedes all data access (ADR-012) | 02 · **WS** | 312(a)(1) | 25 | A.8.3 | CC6.3 |
| EDPF-PRV-001 | Crypto-shredding erasure; erasure itself audited | 22 · **WS** | — | 17 | A.8.10 | P4.2 |
| EDPF-PRV-002 | Erasure survives audit/retention conflict (chain intact post-shred) | 19, 22 · **WS** | 530(j) | 17+5(1)(e) | A.8.10 | P4.2 |
| EDPF-PRV-003 | Consent-linked processing, purpose evaluation | 22 | 508 | 6, 7 | A.5.34 | P2.1 |
| EDPF-PRV-004 | De-identification / tokenization for secondary use | 22 | 514(b) | 4(5) | A.8.11 | P4.3 |
| EDPF-IAM-001 | JWT/OIDC authentication at the pipeline edge | 21 · **WS** (JWT) | 312(d) | 32 | A.5.16 | CC6.1 |
| EDPF-IAM-002 | Policy-based RBAC/ABAC authorization before handlers | 21 · **WS** (RBAC) | 312(a)(1) | 25 | A.5.15 | CC6.3 |
| EDPF-IAM-003 | Break-glass access: time-bounded, audited | 21 | 312(a)(2)(ii) | 9(2)(c) | A.5.15 | CC6.1 |
| EDPF-OBS-001 | Correlation id on every log/trace/audit/error | 05 · **WS** | 312(b) | — | A.8.15 | CC7.2 |
| EDPF-OBS-002 | No classified data in logs, traces, metrics, errors | 05 · **WS** | 502 | 5(1)(f) | A.8.15 | CC6.5 |
| EDPF-ERR-001 | RFC 9457 error contracts; detail limited per code (§10.2) | 18 · **WS** | — | — | A.8.26 | CC7.3 |
| EDPF-RES-001 | Region-pinned routing; cross-region refusal (EDPF-CMP-6002) | 12, 22 | — | 44–49 + DPDP | A.5.14 | CC6.7 |
| EDPF-CON-001 | Transactional outbox — no silent partial cross-store writes | 09 · **WS** | 312(c)(1) | 5(1)(f) | A.8.26 | PI1.2 |
| EDPF-CON-002 | Idempotency keys — retries cannot duplicate clinical acts | 09 · **WS** | 312(c)(1) | 5(1)(d) | A.8.26 | PI1.2 |
| EDPF-SDL-001 | SAST, secret scan, SBOM + license gate on every commit | 01, 34 · **WS** (CI) | — | 32 | A.8.28 | CC8.1 |
| EDPF-SDL-002 | Public API change control (baseline-diffed in CI) | 01 · **WS** | — | — | A.8.28 | CC8.1 |

Standards named in the original specification and not yet mapped here
(SOC 2 availability series, ISO 22301, PCI-DSS full set, FHIR/ABDM
interoperability certifications) are scheduled against Phases 24b, 29, 30 and
36; each lands in this matrix in the phase that implements its controls —
never before (a matrix row without an implementing phase is a claim, not a
control).
