# EDPF — Enterprise Data Platform Framework

A modular, database-agnostic data platform framework for regulated
enterprises, on Clean Architecture in C#. Domain focus: healthcare
(HIS / EMR / LIS / RIS / Pharmacy / Billing / Insurance), extensible to ERP,
CRM, HRMS, Finance, Insurance, Manufacturing, Retail, Government, Banking,
Logistics, Education.

> **What EDPF is for.** Its defensible product is **compliance, tenancy and
> audit** — not data access. The data layer is the thinnest viable wrapper
> over libraries other people maintain (ADR-001), so engineering effort lands
> where nothing off the shelf helps.

> **On compliance.** EDPF ships *controls that enable* HIPAA / GDPR / SOC 2 /
> ISO compliance. A framework cannot make an application compliant —
> compliance is a property of the system, its processes, and its
> organization. Neither the documentation nor the marketing may claim
> otherwise.

## Current state

**Waves 0 and 1 complete; Wave 2 contracts complete.** Gate G0 (Viability) and
Gate G1 (Foundation) passed on engineering criteria.

| | |
|---|---|
| ADRs accepted | 37 accepted, 1 proposed ([ADR-037](docs/adr/ADR-037-v1-scope-boundary.md) — v1.0 scope, awaiting sponsor) |
| Target frameworks | 4, and **all four execute tests** — net6.0 was retired ([ADR-038](docs/adr/ADR-038-retire-tier-2.md)): unsupported since Nov 2024, and untestable with the pinned runner |
| Automated tests | 1412 executions green without Docker (1368 tests, plus the 11-test runtime suite on each of 4 frameworks); 24 Tier A parity tests need a Docker daemon |
| SQL providers | 4 dialects (SQL Server, PostgreSQL, SQLite, MySQL); 2 verified against live engines |
| Host types | 2 (ASP.NET Core Web API, Worker Service) |
| Injection corpus | 226 assertions, zero successful injections |
| Adversarial isolation routes | 12/12 covered, coverage machine-checked, 48 tests |
| Live gate demonstrations | 24/24 against SQL Server LocalDB, plus 24/24 across both Tier A providers via Testcontainers — re-verified 2026-08-03 |
| Public API surface | tracked, diffed in CI |

| Wave | Phases | Gate |
|---|---|---|
| 0 — Inception & Proof | 00 Discovery · 01 Foundations · 02 Walking Skeleton | G0 ✔ |
| 1 — Platform Core | 03 Configuration & Secrets · 04 DI & Composition · 05 Observability | G1 ✔ |
| 2 — Data Access Core | 06 Providers · 07 Routing · 08 Query · 09 Consistency · 10 Repository · 11 Migrations | G2 — contracts done, [engine-bound verification outstanding](docs/phases/p06-p11-data-access-core/14-completion-report.md#carried-forward-to-gate-g2) |
| 3 — Data Services | 12 Tenancy · 13 Bulk · 14 Blob · 15 Cache · 16 Search · 17 Validation | G3 — contracts done, [backend verification outstanding](docs/phases/p12-p17-data-services/14-completion-report.md#carried-forward-to-gate-g3) |
| 4 — Trust | 18 Errors · 19 Audit · 20 Crypto · 21 AuthN/Z · 22 Compliance | G4 — [independent reviews outstanding](docs/phases/p18-p22-trust/14-completion-report.md#carried-forward-to-gate-g4) |
| 5 — Data Platform & Domain | 23 Classification · 24 Clinical safety | G5 — [interop verification outstanding](docs/phases/p23-p24-data-platform-domain/14-completion-report.md#carried-forward-to-gate-g5) |
| 6 — Integration & Runtime | 25 Scheduling · 26 Messaging · 27 i18n · 28 Feature flags | G6 — [broker verification outstanding](docs/phases/p25-p28-integration-runtime/14-completion-report.md#carried-forward-to-gate-g6) |
| 7 — Operate | 29 DR · 30 SLOs · 31 Performance · 32 Test matrix | G7 — [drills and matrix outstanding](docs/phases/p29-p32-operate/14-completion-report.md#carried-forward-to-gate-g7) |
| 8 — Productize | 33 CLI & docs · 34 Packaging · 35 Reference apps | G8 — [external usability test outstanding](docs/phases/p33-p35-productize/14-completion-report.md#carried-forward-to-gate-g8) |
| 9 — Harden & Release | 36 Security validation · 37 Release candidate | G9 — [external validation outstanding](docs/phases/p36-p37-harden-release/14-completion-report.md#gate-g9--the-honest-assessment) |

**All nine waves worked.** See [PROGRAMME-STATUS.md](docs/PROGRAMME-STATUS.md)
for what exists, what does not, and the six things a sponsor must do next —
none of which are engineering.

### Six platforms added after the waves

Built on the sponsor's instruction, against [ADR-037](docs/adr/ADR-037-v1-scope-boundary.md)'s
v1.0 list and past two of its deferrals. Each follows the same shape the rest
of the framework uses — a thin technology adapter under a policy layer that is
written once:

| Platform | What it contributes | What it deliberately is not |
|---|---|---|
| **Storage** | Tenant enforcement, encryption at rest, content-type coercion, bounded reads, platform-computed hashing — applied to every backend | Not a cloud SDK. Filesystem and in-memory ship; S3/Azure/SFTP are optional packages |
| **Communication** | Consent before send, a per-channel classification ceiling, closed-grammar templates, addresses that reject rather than repair | Not a mail server. A pickup-directory channel ships; vendor APIs are optional packages |
| **Workflow** | A state machine validated before it runs — determinism, reachability, no dead ends, terminal means terminal | Not a BPM engine. No parallel branches, sub-processes, compensation or timers |
| **AI** | Declared use cases with EU AI Act risk tiers, a provider classification ceiling, instruction/data separation, an audit log of metadata | Not a model client, and never will be. Clinical decision support is refused outright ([ADR-023](docs/adr/ADR-023-integrate-do-not-build.md)) |
| **MySQL provider** | The fourth dialect, and the one that disagrees silently — `\|\|` is logical OR there | — |
| **Worker Service host** | Tenancy re-established from the message, since there is no request to carry it | — |

Adopting EDPF? Two documents before any code:

- [**Brownfield adoption strategy**](docs/adoption/brownfield-strategy.md) — how
  an existing HIS moves onto EDPF one bounded context at a time, which cutover
  stages are reversible, and [when not to adopt](docs/adoption/brownfield-strategy.md#when-not-to-adopt).
- [**Shared-responsibility model**](docs/compliance/shared-responsibility-model.md) —
  three of the four responsibility layers are yours, and that is what compliance
  consists of.

## The `edpf` CLI

```bash
dotnet build tools/Edpf.Cli -c Release
```

| Command | What it does |
|---|---|
| `edpf doctor` | Checks for conditions that break EDPF at runtime, not build time — missing tzdata, invariant globalization, committed signing keys |
| `edpf classify-schema <csv>` | Scans a data sample for unclassified PII/PHI and reports classification drift. Exits non-zero on a check-digit-confirmed finding, so it gates a merge |
| `edpf check-licenses <csv>` | Licence-policy gate; a non-compliant transitive licence fails the build (ADR-009) |
| `edpf check-api <old> <new>` | Diffs public-API baselines and reports the SemVer bump the change requires |

> **Regulatory boundary.** EDPF provides data infrastructure and does not make
> clinical decisions. A consumer building clinical decision support on EDPF is
> building a regulated medical device (FDA SaMD / EU MDR) and carries those
> obligations. See [ADR-023](docs/adr/ADR-023-integrate-do-not-build.md).

## Quick start

```bash
dotnet build Edpf.slnx -c Release
```

Run **everything**, including the Tier A provider-parity suite. Needs Docker:

```bash
dotnet test Edpf.slnx -c Release
```

Without Docker, the parity suite is skipped:

```bash
dotnet test Edpf.slnx -c Release --filter "Category!=RequiresDocker"
```

> **Know what that filter costs you.** It skips the identical gate
> demonstrations against SQL Server *and* PostgreSQL — the only tests that
> compare two providers. Running them for the first time found an audit chain
> that could never verify on PostgreSQL, and an idempotent replay that returned
> a different response shape from the original ([ADR-036](docs/adr/ADR-036-stored-form-must-equal-served-form.md)).
> Both had sat in work reported complete since Wave 2. Use the filter when you
> must, not by default.

Both commands above **do** run the tiered runtime suite on every framework —
`dotnet test` cross-targets, so all four appear as separate result lines. To
run just that suite:

```bash
dotnet test tests/Edpf.RuntimeTests -c Release
```

> **A correction, because the earlier claim here was wrong.** This section used
> to say a solution-level `dotnet test` executes only one framework per project
> and each had to be named. It does not. What was actually happening is in the
> [suite's project file](tests/Edpf.RuntimeTests/Edpf.RuntimeTests.csproj):
> `tests/Directory.Build.props` sets the *singular* `<TargetFramework>`, which
> silently wins over a project's *plural* `<TargetFrameworks>` and disables
> cross-targeting altogether. Only one framework was being **built**, so only
> one could run — and the missing framework was misread as a limitation of the
> test runner. CI still enumerates them one at a time, which is worth doing for
> a different reason: it fails loudly if a declared framework cannot execute,
> rather than quietly producing one fewer result line.
>
> Eleven assemblies declare net472/net48 support (ADR-002) and, until this
> suite existed, not one line of them had ever *executed* there — every test
> project targeted net10.0 only. Fixing that named net48, and stopped: on
> 2026-08-04, asking the same question of the whole matrix found that **two of
> five declared frameworks had ever run a test.** net472 and net8.0 had not.
> All four now pass 11/11, identically.
>
> The fifth, net6.0, could not be made to run at all — `xunit.runner.visualstudio`
> 3.1.5 resolves .NET Framework assets for it. It was retired
> ([ADR-038](docs/adr/ADR-038-retire-tier-2.md)) rather than tested, having also
> been out of Microsoft support since November 2024.
>
> CI derives this list from `EdpfLibraryTargetFrameworks` rather than repeating
> it, because a hand-kept list is exactly what went stale.

Run the walking skeleton and its gate demonstration —
see [samples/walking-skeleton](docs/phases/p02-walking-skeleton/11-usage.md)
for the full instructions, including the no-Docker path:

```bash
docker compose -f samples/walking-skeleton/docker-compose.yml up -d
```

```bash
dotnet run --project samples/walking-skeleton/Edpf.WalkingSkeleton.Api --framework net10.0
```

```powershell
./samples/walking-skeleton/gate-demonstration.ps1
```

## What the skeleton proves

One authenticated request creates a `Patient` under tenant A. A correctly
authorized user of tenant B asking for that same id gets **404, not 403** —
because leaking existence is itself a leak. The patient's medical record
number is ciphertext in the raw table, wrapped in a self-describing 35-byte
envelope. Destroying the subject's key makes the data unrecoverable while the
tamper-evident audit chain still verifies. One outbox message rides the same
transaction and dispatches exactly once. Every failure is an RFC 9457
document carrying the correlation id that ties the whole request together.

Wave 1 adds the systems underneath: the container refuses to start if any
singleton captures a scoped service, secrets render as `***` through every
route including interpolation and serialization, a bad configuration reload is
rejected rather than half-applied, liveness stays up when the database goes
down, and no PHI reaches a log sink by any of ten adversarial routes.

Wave 2 makes two properties structural rather than disciplinary. **Injection
is unrepresentable**: identifiers come from metadata and are rejected if
illegal rather than escaped, operators come from a closed enum, and values are
always parameters — so a hostile payload and a benign one compile to
byte-identical SQL. **The tenant predicate is unavoidable**: it is emitted
first, unconditionally, and an unresolved tenant is refused rather than read as
"all tenants".

Wave 3 extends that to the paths that leak in practice. A **cross-tenant blob
path cannot be constructed**; traversal is rejected rather than normalised. An
**unprefixed cache key cannot be constructed** for a tenant-scoped entity —
the leak that looks obviously correct as `"patient:" + id`. A **validation
failure cannot echo attacker input**. And a thousand concurrent requests on an
expired cache key make exactly one origin call.

Wave 4 is the trust layer. "Not found" and "not authorized" present
**identically** outward, so errors are not an enumeration oracle.
De-identification covers all eighteen HIPAA Safe Harbor identifiers, removes
unmapped fields by default, and shifts dates per-subject so clinical intervals
survive while absolute dates do not. Authorization scopes run
org → facility → department → unit → resource in one model, with containment
matched on whole segments so `cardio` does not silently include `cardiology`.

Waves 5 and 6 handle the errors that reach patients and operators. A dose
conversion **refuses** to cross dimensions or guess an unknown unit, because
5 mg given as 5 mcg is a thousand-fold error. A DST-ambiguous scheduled time
is reported as skipped or repeated rather than silently resolved. Currency
minor units are per-currency — hardcoding two decimals breaks yen and Kuwaiti
dinar in opposite directions. And `"FILE".ToLower()` in Turkish gives `fıle`,
which is why identity comparison here is always ordinal.

All of it runs on both SQL Server and PostgreSQL, on two runtimes, in CI.

## Repository layout

```text
src/          Edpf.Abstractions (contracts, zero deps) · Edpf.Core (shared
              kernel) · Edpf.Compatibility (the only #if) · Edpf.Diagnostics ·
              Edpf.Metadata (compile-time and runtime-defined fields, one
              model — ADR-025) · Edpf.Formula (sandboxed decimal expression
              evaluation — ADR-026) · Edpf.Rules (decision tables — ADR-027) ·
              Edpf.Barcode (GS1 traceability encoding) · Edpf.DataQuality ·
              Edpf.Devices (Tier 3 — the net472/net48 justification) ·
              Edpf.Connectors (gap-free incremental sync — ADR-030) ·
              Edpf.Migration (brownfield verification — ADR-032) ·
              Edpf.Reporting (export as a security boundary — ADR-033) ·
              Edpf.Licensing (offline entitlement — ADR-034)
providers/    per-engine providers, licence-isolated (Wave 2)
verticals/    Edpf.Healthcare.Domain — optional; consumes only the public
              surface, and the core never references it back (ADR-024)
tests/        UnitTests · ArchitectureTests · ConformanceTests ·
              WalkingSkeleton.Tests · Benchmarks
samples/      reference applications; the walking skeleton lives here
tools/        CLI, generators, diagram emitter (Phase 33)
docs/         adr/ · tdl/ · phases/ · compliance/
```

## Documentation

| Start here | |
|---|---|
| [Appendix Z](docs/standards/appendix-z-implementation-standards.md) | The engineering rulebook — read on day one, return to for every PR |
| [Architecture decisions](docs/adr/README.md) | The thirty-six binding decisions, each with its revisit triggers |
| [Phase folders](docs/phases/) | What each phase delivered, verified, and decided |
| [Compliance controls](docs/compliance/compliance-control-matrix.md) | Control → HIPAA/GDPR/ISO/SOC 2 clause mapping |
| [Data classification](docs/compliance/data-classification.md) | Handling rules per level |
| [Threat model](docs/phases/p00-discovery-decisions/07-security.md) | 18 STRIDE entries with their controls |
| [Contributing](CONTRIBUTING.md) | Branching, commits, review, the onboarding path |
| [Security policy](SECURITY.md) | Reporting a vulnerability |

## Non-negotiables

1. **Decisions before code.** Every phase restates the ADRs that constrain it.
2. **Vertical slice before horizontal scale.** Generalize only what a working
   slice has already proven.
3. **Every requirement carries a number.** No "fast" or "scalable" without a
   target and a measurement method.
4. **Compliance = controls, not claims.**
5. **Prefer buy over build** (Principle 0): build only what differentiates;
   wrap everything else so it can be replaced.
6. **Continuous, not late:** testing, observability and security in every
   phase.
7. **Production bar:** XML docs, structured logging, no TODOs, no
   placeholders, no hardcoded values, tests included.

## Licence

Dual: source-available core plus a commercial enterprise licence, with
restrictive third-party drivers isolated in optional packages
([ADR-009](docs/adr/ADR-009-licensing.md)). See [LICENSE.md](LICENSE.md).
