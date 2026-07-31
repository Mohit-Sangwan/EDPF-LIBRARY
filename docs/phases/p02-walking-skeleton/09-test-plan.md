# Phase 02 — Test plan and gate demonstration mapping

Each of the ten demonstrations required by Playbook Phase 02 §⑤ maps to an
automated test. Nothing in this gate is verified by inspection.

| # | Demonstration | Automated test | Live script |
|---|---|---|---|
| 1 | Authenticated request creates a `Patient` under tenant A | `Demo1_AuthenticatedCreate_Returns201WithLocation` | check 1 |
| 2 | Tenant B, correctly authorized in its own tenant, cannot read it — **404, not 403** | `Demo2_TenantB_ReadingTenantAPatient_Gets404Not403` | check 2 |
| 3 | A single trace spans API → auth → repository → database with one correlation id | `Demo3_SuppliedCorrelationId_RoundTripsOnResponse` + Jaeger UI | check 3 |
| 4 | Audit record exists, hash chain validates, contains **no raw PHI** | `Demo4_AuditChain_VerifiesAndHoldsNoRawPhi`, `Write_Subject_NeverStoresRawIdentifier` | check 4a |
| 5 | The PHI field is unreadable in the raw database — ciphertext on inspection | `Demo5_RawDatabase_HoldsCiphertextNotPlaintext` | checks 5a, 5b |
| 6 | Destroy the subject DEK; data unrecoverable; audit chain still validates | `Demo6_Erasure_MakesDataUnrecoverable_ChainStillValid`, `Verify_AfterSubjectErasure_ChainStillValid` | checks 6a, 6b |
| 7 | An outbox message is present and dispatched exactly once | `Demo7_OutboxMessage_DispatchedExactlyOnce` | checks 7a, 7b |
| 8 | Forced failure returns well-formed RFC 9457 carrying the correlation id | `Demo8_ValidationFailure_YieldsProblemDetailsWithCorrelationId` | checks 8a–8d |
| 9 | Identical behavior on both providers, both TFMs | `SqlServerGateDemonstrations` / `PostgreSqlGateDemonstrations`; both TFMs built and tested in CI | — |
| 10 | Baseline load test records the first benchmark entry | `tests/Edpf.Benchmarks` (`CryptoEnvelopeBenchmarks`) | — |

Additional negative and boundary coverage in the same suite:
idempotent replay and key-reuse conflict; 401 without a token; 403 without
the role; 404 without a tenant header; paged-list metadata; and the ADR-012
composed-order assertion.

## Component tests (no container required)

`tests/Edpf.WalkingSkeleton.Tests/Component` runs the real security and audit
implementations against an in-memory relational store: envelope round-trip,
nonce uniqueness, destroyed-key tombstones, per-subject erasure isolation,
tamper detection on a modified event type, chain break on a deleted record,
and token stability. These run on every commit, everywhere.

## Execution record — 2026-08-01

**Automated suites:** 84 tests green (59 unit, 12 architecture, 12
walking-skeleton component, 1 conformance contract).

**Live gate run:** executed against SQL Server LocalDB via
[`gate-demonstration.ps1`](../../../samples/walking-skeleton/gate-demonstration.ps1)
— **19 checks passed, 0 failed**. Selected evidence:

- Cross-tenant read of a known-good id returned **HTTP 404** (never 403).
- Raw `PATIENT.MrnEnvelope` inspected directly over SQL:
  `010100340A62A2E64D174FBBC5F5952887C06801…`, 65 bytes for a 20-byte MRN —
  the 35-byte ADR-007 header plus ciphertext and tag, with no plaintext
  subsequence present.
- After erasure the read returned `medicalRecordNumber = "[erased]"` **and**
  chain verification still reported `isValid = true` across 10 records —
  ADR-006's three-way reconciliation, demonstrated rather than argued.
- Exactly one outbox row for the created patient, `Attempts = 1`,
  `DispatchedUtc` set.
- Validation failure returned `application/problem+json` with
  `errorCode = EDPF-VAL-1001`, a correlation id, and the stable type URI.

**Containerized run** (`Category=RequiresDocker`, both Tier A providers) is
wired in CI and runs on any machine with a reachable Docker daemon; it was
not executed on the authoring workstation, where the Docker engine could not
be started without elevation. The LocalDB run above exercises the identical
SQL Server provider path.
