# Phase 00 — Vision, scope and quantified NFRs

## Vision & scope

**EDPF** is a modular, database-agnostic data platform framework for
regulated enterprises, built on Clean Architecture in C#. Its defensible
product is **compliance, tenancy and audit** — not data access (Principle 0).
The data layer is the thinnest viable wrapper over libraries other people
maintain (ADR-001), so effort lands where nothing off the shelf helps.

**Primary domain:** healthcare (HIS / EMR / LIS / RIS / Pharmacy / Billing /
Insurance), extensible to ERP, CRM, HRMS, Finance, Insurance, Manufacturing,
Retail, Government, Banking, Logistics, Education.

**Explicit non-goals for v0.9:**

- Not an ORM, not a BI tool, not an application. EDPF is consumed by
  applications; it never ships as one.
- No cross-store distributed transactions (ADR-003) — architecturally
  refused, not deferred.
- No claim that any application "is compliant" — controls only (Golden
  Rule 5).
- Post-1.0 (Appendix H.7): agent/MCP hosting, offline sync, SDK generation
  for non-.NET languages.

**Design partners:** two named healthcare providers, one Tier-1 hospital
group (IN) and one clinic network (EU), giving both DPDP and GDPR residency
exposure from day one. Contractual specifics are program-level (Appendix F).

## NFR sheet

Every entry carries a number and a measurement method (Golden Rule 4). These
are the acceptance targets Phase 31 measures against; they are amendable only
by Sponsor + Chief Architect with measurement showing the target is wrong
(Z.21).

| NFR | Target | Measured by |
|---|---|---|
| Read latency p50 / p95 / p99 (single entity, encrypted field, tenant-scoped) | 5 / 25 / 60 ms | Load test, Phase 31 |
| Write latency p99 (create + outbox + audit, one transaction) | 120 ms | Load test, Phase 31 |
| Sustained throughput | 20 000 req/s per node (8 vCPU) | NBomber/k6, Phase 31 |
| Concurrent connections per node | 10 000 | Pool saturation test, Phase 07 |
| Availability SLO | 99.95 % monthly (≈22 min error budget) | Phase 30 SLO evaluator |
| RTO / RPO | 4 h / 15 min | DR drill, Phase 29 |
| Cold start (serverless host) | < 1.5 s p95 | Azure Functions bench, Phase 35 |
| Max allocation per request | < 8 KB | BenchmarkDotNet MemoryDiagnoser |
| Bulk insert throughput | 500 000 rows/min (SQL Server, Tier A) | Phase 13 benchmark |
| Audit write overhead | < 3 % of p99 | Phase 19 benchmark |
| Encryption overhead per field (≤ 1 KB) | < 50 µs | Z.18 crypto benchmark |
| Key resolution (cached / uncached) | < 10 µs / < 5 ms | Z.18 key benchmark |
| Outbox dispatch lag p99 under load | < 5 s | Phase 30 SLO |
| Tenant resolution overhead | < 1 ms p99 | Phase 12 benchmark |
| Commit-gate CI duration | ≤ 15 min | CI telemetry (Z.13) |

**Scale envelope:** 10 000 tenants per deployment; 100 M patient records per
tenant at the top decile; 5 GB maximum single blob (DICOM studies);
20-year retention horizon (drives ADR-007).

## Requirements traceability

Functional and business requirements (FR/BR) trace from Book 6 Part 2. The
subset the walking skeleton demonstrates is described in
[p02/00-README.md](../p02-walking-skeleton/00-README.md), and what it was
verified to do is in
[p02/14-completion-report.md](../p02-walking-skeleton/14-completion-report.md).
