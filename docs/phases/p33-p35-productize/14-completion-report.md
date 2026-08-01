# Wave 8 — Completion report (toward Gate G8: Adoptable)

**Phases:** p33–p35 · **Date:** 2026-08-01 · **Squads:** C + DevSecOps

## What was built

As expected, this wave was back in buildable territory — tooling and
packaging governance are things that can be produced *and demonstrated* here,
rather than described.

| Phase | Delivered | Location |
|---|---|---|
| **33** | The `edpf` CLI: `doctor`, `classify-schema`, `check-licenses`, `check-api` — packaged as a `dotnet tool` | `tools/Edpf.Cli/` |
| **34** | `LicensePolicy` gate, `ApiCompatibilityGate` SemVer diff | `src/Edpf.Operations/SupplyChain/` |

## Verification — demonstrated, not asserted

**792 automated tests green** — 712 unit, 47 isolation, 18 architecture, 12
walking-skeleton component, 3 conformance contract.

Every CLI command was **run**, and each caught what it claims to catch:

**`doctor`** — five checks against this repository, all passing: ICU
globalization present, clock plausible, IANA time-zone data resolvable, no
signing keys committed to configuration, public-API baselines tracked. The
checks were chosen from failures that are cheap to detect and expensive to
diagnose in production — a slim container without tzdata breaks every local
time conversion at runtime, and invariant globalization breaks collation
*silently*.

**`classify-schema`** — run against a planted sample and it behaved exactly
as designed on all three cases at once:

```
DRIFT   ClinicalNote: looks like NhsNumber, should be classified Phi
```

It found the NHS number hiding in a free-text clinical note declared merely
`Internal`; it did **not** flag the payment reference, which was correctly
declared `Pci`; and it did **not** false-positive on a sixteen-digit order
number, because that fails Luhn. That last one is the whole precision
argument in a single line of output — a classifier that flagged it would be
muted within a week.

**`check-licenses`** — caught all three violation classes in one run: a
transitive GPL-3.0 dependency (forbidden outright), an undeclared licence
(fails closed, because an unclassified licence is one nobody has read), and
MPL-2.0 in the core graph (permitted only in an optional package, ADR-009).
Each violation names `[transitive]` where applicable, because nobody adds
strong copyleft deliberately — it arrives four levels down a chain somebody
added for a date formatter, and the reader needs telling.

**`check-api`** — given an `int` → `long` signature change proposed as a
minor bump:

```
REQUIRED VERSION BUMP: MAJOR
Proposed bump 'Minor' is INSUFFICIENT; this change requires 'Major'.
```

A signature change is treated as a removal plus an addition, which is
correct: a consumer compiled against the old signature does not care that
something similarly named still exists.

## Why the CLI has no dependencies

Argument parsing is hand-rolled rather than taken from a parser library. The
command surface is small and stable, and this tool ships **to consumers** —
every dependency it carries becomes a licence obligation and a CVE surface
for everyone who installs it. A tool whose job includes enforcing a licence
policy should be able to pass its own gate.

## Carried forward to Gate G8

| Requirement | Phase | Why not now |
|---|---|---|
| Remaining CLI verbs (`scaffold`, `migrate`, `generate-model`, `di-graph`, `verify-audit-chain`, `rotate-keys`, `provision-tenant`) | 33 | Each needs the subsystem it drives to be live — a database, a key vault, a running host |
| `dotnet new` templates per host type | 33 | Needs the Phase 35 reference applications to template from |
| Analyzers packaged for consumers | 33 | The rules exist as architecture tests; converting them to a shipped Roslyn analyzer package is its own build |
| DocFX API reference, guides, compiled documentation samples | 33 | Substantial writing; "every code sample compiled in CI" needs the samples to exist first |
| NuGet signing, SLSA L3 provenance, private feed, staged promotion | 34 | Needs signing certificates and a release pipeline |
| SBOM in both CycloneDX **and** SPDX | 34 | CycloneDX is wired in CI; SPDX is not |
| **All twelve reference applications** | 35 | Only the non-shippable walking skeleton exists |
| WCAG 2.2 AA verification | 35 | Needs the UI references |
| **Gate G8: an external team builds a working app in under a day** | 35 | Requires an external team. Cannot be self-assessed, by construction |

**Gate G8 is NOT passed**, and its criterion is one I want to be precise
about: *"an external team, given only the public documentation and templates,
builds a working multi-tenant, audited, encrypted application in under one
day. If they cannot, the problem is the framework's ergonomics or the
documentation — not the team."*

That is a usability test with real strangers. I cannot run it, cannot
simulate it, and cannot approximate it by reading my own documentation and
finding it clear — which is precisely the bias the criterion is written to
defeat. It joins the independent security and cryptographic reviews (Gate G4)
in the category of criteria that no amount of further work *here* can satisfy.
