# Phase 23d — Data Quality Platform

**Status:** Complete
**Gate contribution:** G5 (Domain Capability) · closes F22
**ADR produced:** [ADR-028 — Profiling discloses nothing](../adr/ADR-028-profiling-discloses-nothing.md)

## What this phase delivers

> *"NABH and NABL both assess data quality; without measurement there is no
> way to demonstrate it."*

| File | Contents |
| --- | --- |
| [`DataProfiler.cs`](../../src/Edpf.DataQuality/DataProfiler.cs) | Column profiling with classification-aware and small-cell suppression |
| [`ColumnProfile.cs`](../../src/Edpf.DataQuality/ColumnProfile.cs) | The findings, separating shape from content |
| [`QualityScore.cs`](../../src/Edpf.DataQuality/QualityScore.cs) | Six dimensions scored separately, each carrying its method |
| [`QualityGate.cs`](../../src/Edpf.DataQuality/QualityGate.cs) | Quarantine-on-import thresholds |
| [`DataCleaner.cs`](../../src/Edpf.DataQuality/DataCleaner.cs) | Cleansing with a reversible before/after trail |
| [`StringSimilarity.cs`](../../src/Edpf.DataQuality/StringSimilarity.cs) | Levenshtein, Jaro, Jaro-Winkler for duplicate detection |

## The finding this phase turns on

**A quality report is an artefact that leaves the building.** It gets emailed
to a vendor, pasted into a ticket, attached to a steering deck, and left on a
dashboard that outlives the project. It travels much further than the database
does, through channels with none of the database's controls.

And a profile of a classified column is a *projection of that column*. A "ten
most common values" report over a medical record number column **is** the
medical record numbers. An inferred pattern matching exactly one value **is**
that value.

So a profile separates two kinds of finding:

- **Shape** — row count, null rate, cardinality, min/max length. Always
  reported; discloses nothing about any individual.
- **Content** — sample values, inferred patterns. Withheld for any classified
  column, and `ValuesWithheld` records that they were, because an empty value
  list otherwise reads as an empty column.

**Small-cell suppression applies regardless of classification.** In a cohort
of 400, "one patient has this postcode" identifies that patient even though a
postcode alone is not PHI. Default threshold 5 — the convention most
disclosure-control guidance converges on, and a constructor argument rather
than a constant because it is a convention, not a proof.

The profiler asks `ProtectionPolicy` — the same table the redactor asks
(ADR-025) — so it cannot form its own opinion about what is sensitive and
drift from it. `Profiler_AgreesWithTheRedactorAboutWhatIsSensitive` checks all
six classification levels.

## Cleansing: the opposite constraint

> *"Cleansing clinical data is a change to the medical record and must be
> traceable."*

Standardising an address or trimming a name looks like housekeeping. It is an
amendment to a record a clinician may later rely on, made by a process rather
than a person — which makes it *more* in need of a trail, not less.

So the trail holds before-values **in full, including for classified fields**,
and carries the field's classification so storage and export protect it to the
same standard. A trail that redacts the before-value cannot reverse the change,
which is the entire reason it exists.

`OriginalValue` returns the value as it *arrived*, not an intermediate state —
reversing to an intermediate would restore a value that was itself the output
of a rule. A rule that throws leaves the value unchanged and names itself in
the error, so one bad rule cannot take an import down.

## Scoring and gates

Six dimensions — completeness, accuracy, consistency, timeliness, uniqueness,
validity — scored **separately and never averaged**. A single quality
percentage lets a dataset that is 100% complete and entirely stale score the
same as one that is current and half-empty, and those need opposite remedies.

Three decisions that each close a way of passing a gate you should not:

- A dimension assessed over **zero rows scores 0, not 1** — an empty dataset is
  not a perfect one, and scoring it perfect is how a broken import passes.
- A **required-but-unassessed** dimension fails — otherwise a gate is bypassed
  by not running the check, which is the easiest bypass there is and the one
  that looks like an accident.
- `WeakestScore` is the **minimum**, so a perfectly complete and entirely
  invalid dataset cannot average its way through.

Below-threshold data is **quarantined** — not rejected (which loses it, and the
sender finds out too late) and not ingested-with-a-warning (which is worse: the
bad data becomes indistinguishable from the good and every consumer inherits
it; a warning in a log is not a control).

## Exit criteria

| Criterion | Status |
| --- | --- |
| Column profiling: distribution, cardinality, null rate, pattern inference | Met |
| Six quality dimensions scored explicitly | Met |
| Rule-based cleansing with full before/after audit | Met — and reversible |
| Fuzzy matching feeding entity resolution | Met — as a similarity, not a decision |
| Quality gates that quarantine rather than ingest | Met |

## Scope boundary

**String similarity produces a number, not a match decision.** The threshold at
which two records are the same person needs a labelled set built by people who
know the domain: a false merge puts two people's allergies on one chart
(Phase 24b's clinical-safety incident); a false non-merge leaves a duplicate.
Those costs are not symmetric, so the threshold is not a technical choice and
is not shipped. This is the same reason Wave 5 declined to report
entity-resolution precision and recall.

Similarity implementations are verified against published examples —
`MARTHA`/`MARHTA` = 0.961, `DIXON`/`DICKSONX` = 0.813, `DWAYNE`/`DUANE` = 0.840
— rather than against what this implementation produces.

**Aggregate statistics are not proven non-disclosive.** Null rate and
cardinality on one column disclose nothing about an individual, but
differencing attacks across profiles of overlapping cohorts are a known
technique and nothing here defends against them. Formal disclosure control —
k-anonymity, differential privacy — is a separate discipline and is not
implemented. ADR-028 records this rather than leaving it to be discovered.

**Standardization of addresses, names and phone numbers is not shipped.** The
phase names it, and `CleansingRule` is the mechanism, but the rules themselves
are locale-specific reference data: a name standardiser calibrated on one
country's conventions mangles another's, and a wrong one is a corrupted medical
record with an audit trail explaining exactly who corrupted it.
