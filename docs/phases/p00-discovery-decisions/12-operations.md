# Phase 00 — Risk register

Seeded with the killers from the gap analysis (Book 1 Part B) and the
program-readiness items (Appendix F). Reviewed at every stage gate.

Severity: **C** = project-killing · **H** = high · **M** = medium.

| # | Risk | Sev | Owner | Mitigation | Status |
|---|---|---|---|---|---|
| R1 | Data-core build-vs-buy defaults to the most expensive path | C | Chief Architect | ADR-001 wraps EF Core/Dapper/native SDKs | **Closed** |
| R2 | net472 ↔ net10 contradiction blocks the build | C | Chief Architect | ADR-002 tiered surface; `#if` confined to `Edpf.Compatibility`; all five TFMs build green | **Closed** |
| R3 | Bottom-up plumbing with no vertical slice | C | Chief Architect | Phase 02 walking skeleton, gate demonstrations automated | **Closed** |
| R4 | NFRs unquantified; nothing to design against | C | Sponsor | NFR sheet, zero `[DECISION NEEDED]` remaining | **Closed** |
| R5 | Cross-store 2PC assumed but infeasible | C | Data lead | ADR-003 outbox + saga + idempotency; Spike-D documented the failure | **Closed** |
| R6 | Multi-tenancy strategy unstated | C | Security Architect | ADR-004; isolation proven adversarially in CI | **Closed** |
| R7 | Erasure vs. immutable audit vs. retention unresolved | C | Compliance Officer | ADR-006 crypto-shredding; chain verifies post-shred in CI | **Closed** |
| R8 | Compliance framed as features rather than controls | C | Compliance Officer | Control matrix with clause mapping; Golden Rule 5 in docs and marketing review | **Closed** |
| R9 | Test matrix explosion (13 DB × 5 TFM × N) | H | QA lead | ADR-008 tiering + one conformance suite | **Closed** |
| R10 | Third-party driver licensing blocks shipping | H | Legal | ADR-009 optional isolated packages + SBOM/licence gate | **Mitigated** |
| R11 | Program duration (61 phases) exceeds funding runway | C | Sponsor | Release split: v0.9 at ~14 months (Appendix I.4); stopping rule enforced | **Open — program level** |
| R12 | Brownfield adoption fails; nobody migrates | C | Sponsor / Product | Tier 3 surface (ADR-002); coexistence samples 6–7 (Z.17); design-partner-led | **Open — program level** |
| R13 | Support model and vulnerability response undefined | H | Ops lead | Appendix F.C items; Phase 30/36 deliverables | **Open — program level** |
| R14 | Legal packaging (BAA/DPA) missing at first customer | H | Legal | Appendix F.B; required before design-partner go-live | **Open — program level** |
| R15 | Key custody at per-subject scale degrades p99 | H | Security Architect | Key cache design (Phase 20) with Z.18 benchmark budget | **Open — engineering** |
| R16 | Scope creep reopens the closed specification | H | TSC | Stopping rule (Z.21): new capability requires a blocked named design partner | **Controlled** |
| R17 | Shared-kernel becomes a junk drawer | M | Chief Architect | Additions to `Edpf.Core` need Chief Architect approval + justification | **Controlled** |
| R18 | Skeleton shortcuts promoted to production | M | Squad leads | Marked non-shippable; TDL-0001/0002/0003 record every shortcut; Wave 2 rewrites | **Controlled** |

Items marked **Open — program level** are not engineering problems and will
not be solved by any of the 61 phases (Appendix F's central finding). They
belong to the sponsor and are reviewed monthly.
