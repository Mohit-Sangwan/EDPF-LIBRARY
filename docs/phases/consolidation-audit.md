# Consolidation audit — the eleven Appendix H/I phases

**Date:** 2026-08-02
**Scope:** the nine packages and eleven ADRs added after the Wave 9 report

## Why this exists

Eleven phases landed in sequence, each verified against its own exit criteria.
That is not the same as verifying them against the *programme's* standing
rules. A phase can pass its own tests and still sit outside a gate that every
earlier package is subject to — and nobody notices, because the gate reports
green on the packages it knows about.

Phase 24f had already found one instance: `CoreNeutralityTests` was enforcing
ADR-024 against a list of eleven assemblies while six newer ones sat outside
its scope. All six happened to be neutral, but they were neutral **unguarded**.
This audit looked for the rest.

## What was checked

| Check | Result |
| --- | --- |
| Every `src` project present in the solution | ✅ 22/22 — a project outside the solution is never built by CI and its rules never run |
| Every packable project declares a `Description` | ✅ 22/22 — otherwise the package ships nameless |
| Dependency graph acyclic and layered | ✅ no cycles; everything reaches Abstractions and Core, three reach Metadata, one reaches Formula |
| Source-scanning architecture rules cover new assemblies | ✅ they enumerate `src` rather than a list, so new packages are covered on arrival |
| ADR numbering contiguous, index complete | ✅ 34 files, 34 index rows, no gaps |
| Relative links across `docs/` resolve | ⚠️ one broken — fixed |
| Unused project references | ⚠️ four found — two fixed, two left alone deliberately |
| `edpf doctor` still runs | ✅ 5/5 checks pass |
| Full suite, five target frameworks | ✅ 1,180 green |

## Findings

### 1. A documentation link promised a file that never existed

`docs/phases/p00-discovery-decisions/01-requirements.md` pointed at
`p02-walking-skeleton/01-requirements.md`. That phase folder has a README, a
test plan, a usage guide and a completion report — but no requirements file was
ever written.

Fixed by pointing at the documents that actually carry the content. Worth
noting the failure mode: the sentence read as though a traceability document
existed, and a reader following the link would have concluded the site was
broken rather than that the document was missing.

### 2. Four project references declared and unused

| Reference | Disposition |
| --- | --- |
| `Edpf.Formula` → `Edpf.Metadata` | **Removed.** Copied from a template; formula field references resolve through `IFormulaContext`, which the assembly defines itself |
| `Edpf.Migration` → `Edpf.Metadata` | **Removed.** Comparison rules are declared per field by the migration operator (ADR-032), not derived from metadata — a legacy source's shape is precisely what the new system's metadata does not yet describe |
| `Edpf.Extensions.DependencyInjection` → `Edpf.Configuration` | **Left.** Pre-existing. It uses *Microsoft's* configuration package, not EDPF's, so the reference does nothing in code — but removing it changes what a consumer receives transitively, and that is a composition decision rather than cleanup |
| `Edpf.Operations` → `Edpf.Abstractions` | **Left.** Pre-existing and harmless: `Abstractions` arrives transitively through `Core` regardless |

The two removals were mine, from copying a project file. They widened the
published dependency graph of two packages for no benefit.

### 3. No architecture test was added for unused references

Considered and **rejected**. A rule whose first act is to grandfather two
pre-existing violations is not earning its place, and exemption lists that
start at two tend to grow. The finding is recorded here instead.

## What this audit did not find

No layering violation, no cycle, no package outside the build, no missing
metadata, no gap in the source-scanning rules. The rules that enumerate `src`
rather than a hand-maintained list picked up all nine new packages
automatically — which is the argument for writing them that way, and the
reason the one list-based rule (`CoreNeutralityTests`) was the one that went
stale.

## The standing lesson

**A rule that names its subjects goes stale; a rule that discovers them does
not.** Of the twenty-seven architecture tests, exactly one enumerated a list of
assemblies, and exactly one fell behind. That is a small sample, and it points
the same way both times it has been tested.
