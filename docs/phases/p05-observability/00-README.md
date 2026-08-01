# Phase 05 — Logging, Telemetry & Observability

**Status:** Complete · **Gate:** G1 (Foundation) · **Squad:** C
**Depends on:** Phases 01, 03, 04

## Purpose

Make every request traceable from entry to database and back, on any host, on
any TFM — while guaranteeing that no PII or PHI ever reaches a log sink.
**In a healthcare framework a log file is a HIPAA-relevant artifact**, and
this is the phase where that is either handled properly or lost permanently.

## Constraining ADRs

ADR-012 (pipeline order), ADR-013, ADR-014.
**Produces** [ADR-015](../../adr/ADR-015-telemetry-redaction-policy.md).

## Scope

**In:** logging abstraction, structured schema, redaction, tracing, metrics,
health checks, correlation propagation.
**Out:** SLOs and alerting (Phase 30 — this phase produces the signals;
Phase 30 makes decisions from them).

## Deliverables

| Deliverable | Location |
|---|---|
| `ISensitiveDataRedactor` contract | `src/Edpf.Abstractions/Diagnostics/` |
| Redactor driven by `DataClassification` | `src/Edpf.Diagnostics/Redaction/SensitiveDataRedactor.cs` |
| Standard log schema field names | `src/Edpf.Diagnostics/LogFields.cs` |
| Activity source, meter and header names | `src/Edpf.Diagnostics/EdpfDiagnosticNames.cs` |
| RED + USE instruments | `src/Edpf.Diagnostics/Metrics/EdpfMetrics.cs` |
| Differentiated health checks | `samples/walking-skeleton/.../Program.cs` |
| Ten-route adversarial suite | `tests/Edpf.UnitTests/Diagnostics/AdversarialRedactionTests.cs` |

## Redaction is opt-out, not opt-in

A value is redacted unless it is explicitly known to be safe. Anything
classified Confidential or above is replaced; anything the redactor cannot
recognise is *also* replaced. This is the only default that fails closed:
opt-in redaction breaks the first time someone adds a field and forgets the
attribute, and the consequence of forgetting is a breach.

## Exit criteria — status

- [x] **All ten adversarial redaction routes blocked** — see
      [09-test-plan.md](09-test-plan.md). The suite found a real leak during
      implementation (route 3, exception messages) and the code was fixed, not
      the test.
- [x] Health checks correctly differentiated — liveness never touches a
      dependency, so a database outage drains traffic instead of triggering a
      restart loop. Proven live at G1.
- [x] Correlation intact end-to-end, verified across 25 consecutive requests
      at G1 with zero mismatches.
- [x] **G1 demonstration:** config + DI + telemetry proven together, with
      correlation intact and zero PHI leakage — 24/24 live checks.
- [ ] **Distributed trace across all supported host types** — proven for the
      ASP.NET Core host; the remaining hosts in the Z.17 matrix arrive with
      the reference applications at Phase 35.
- [ ] **Logging overhead under sustained load** — the redactor's cost is
      designed for (member-plan caching) but not yet measured; it belongs to
      the Phase 31 benchmark baseline and is therefore not claimed.
