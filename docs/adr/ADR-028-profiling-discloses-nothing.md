# ADR-028 — A quality report is an artefact that leaves the building; it discloses no values

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 23d — Data Quality Platform
- **Related:** [ADR-025](ADR-025-metadata-resolved-fields.md) (classification source), [ADR-015](ADR-015-telemetry-redaction-policy.md) (redaction policy), [ADR-006](ADR-006-erasure-vs-audit.md) (erasure vs. audit)

## Context

Data quality has to be measured — *"NABH and NABL both assess data quality;
without measurement there is no way to demonstrate it."* Measurement means
profiling: null rates, cardinality, distributions, most common values,
inferred patterns.

**A profile of a classified column is a projection of that column, and it is
easy to miss that.** A "ten most common values" report over a medical record
number column *is* the medical record numbers. An inferred pattern that
matches exactly one value *is* that value. A distribution over a rare
diagnosis in a small cohort re-identifies the patients who have it.

What makes this worse than an ordinary read path is where the artefact goes.
A quality report is exactly the kind of thing that gets emailed to a vendor,
pasted into a ticket, attached to a steering deck, and left on a dashboard
that outlives the project. It travels further than the database ever does,
through channels with none of the database's controls.

The cleansing half raises the opposite problem. *"Cleansing clinical data is a
change to the medical record and must be traceable."* A before/after trail
that redacts the before-value cannot reverse the change — which is the entire
reason it exists.

## Decision

**Profiles disclose shape, never content. Cleansing trails hold content in
full and inherit its classification.**

### Profiling

1. **Aggregate statistics always come back.** Row count, null rate, distinct
   count, min and max length describe a column's shape and disclose nothing
   about any individual. Withholding these would make the profiler useless on
   exactly the columns a steward most needs to assess.

2. **A classified column discloses no values and no inferred pattern.**
   Classification comes from Phase 05b metadata and the redaction decision
   from `ProtectionPolicy` — the same table the redactor consults, so the
   profiler cannot form its own opinion about what is sensitive and drift from
   it.

3. **Small-cell suppression applies regardless of classification.** A value
   identifying fewer than `MinimumCellSize` rows is withheld even in an
   unclassified column: in a cohort of 400, "one patient has this postcode"
   identifies that patient though a postcode alone is not PHI. The default of
   5 is the convention most disclosure-control guidance converges on — a
   convention, not a proof, which is why it is a constructor argument.

4. **Withholding is recorded, never silent.** `ValuesWithheld` tells a reader
   the profile is complete and the values are not theirs to see. Without it, an
   empty value list reads as an empty column.

### Cleansing

5. **The trail holds before-values in full, including for classified fields,
   and carries the field's classification.** A redacted trail cannot reverse a
   change. Marking the record with the classification is what tells storage
   and export to protect it to the same standard as the field it describes.

6. **A no-op records nothing.** A trail padded with unchanged values is a
   trail nobody reads, and the changes that matter get lost in it.

### Scoring

7. **Six dimensions scored separately, never averaged.** A single "quality
   percentage" lets a dataset that is 100% complete and entirely stale score
   the same as one that is current and half-empty — and those need opposite
   remedies. `WeakestScore` is the minimum, so a gate cannot be averaged past.

8. **A dimension assessed over zero rows scores 0, not 1.** An empty dataset
   is not a perfect one, and scoring it perfect is how a broken import passes a
   gate.

9. **A required-but-unassessed dimension fails its gate.** Treating an
   unmeasured dimension as satisfied would let a gate be bypassed by simply not
   running the check — the easiest bypass there is, and the one that looks like
   an accident.

10. **Below-threshold data is quarantined, not rejected and not
    ingested-with-a-warning.** Rejection loses the data and the sender finds
    out too late. Ingestion with a warning is worse: the bad data becomes
    indistinguishable from the good and every downstream consumer inherits it.
    A warning in a log is not a control.

## Consequences

### Accepted costs

- **Stewards cannot see sample values for the columns they most want to
  inspect.** That is the point, and it will be experienced as an obstacle.
  The intended answer is to inspect the data in the system that holds it,
  under that system's controls — not to relax the profiler.
- **Small-cell suppression hides genuine outliers.** A data-entry error
  appearing in one row is invisible to the profile. Validity rules, not
  profiling, are the tool for that.
- **Pattern inference gives up when values disagree.** Reporting the most
  common shape would invite a validation rule that rejects the legitimate
  minority — a Northern Irish postcode, a single-name patient, a hyphenated
  surname.
- **The cleansing trail is as sensitive as the data it describes**, and doubles
  the storage requiring that protection. This is a genuine cost accepted for
  reversibility.

### What this does not claim

- **Aggregate statistics are not proven non-disclosive.** Null rate and
  cardinality on a single column disclose nothing about an individual, but
  differencing attacks across several profiles of overlapping cohorts are a
  known technique and nothing here defends against them. Formal disclosure
  control — k-anonymity, differential privacy — is a different discipline and
  is not implemented.
- **String similarity produces a number, not a match decision.** The threshold
  at which two records are the same person needs a labelled set built by people
  who know the domain: a false merge puts two people's allergies on one chart,
  a false non-merge leaves a duplicate. Those costs are not symmetric, so the
  threshold is not a technical choice and is not shipped.
- **The six dimensions are scored from whatever method the caller supplies.**
  `DimensionScore.Method` carries that method precisely because "Accuracy 94%"
  means nothing on its own — the framework measures what it is told to measure.

## Revisit triggers

- **Someone asks for the suppression threshold to be lowered for a specific
  report.** That is a disclosure decision, not a configuration change, and it
  needs whoever signs off the release to sign off the number.
- **A differencing attack across profiles is demonstrated.** Then aggregate
  statistics need their own budget and this ADR is too weak.
- **A vertical ships a matching threshold.** It must arrive with the labelled
  set it was calibrated against, and that calibration belongs in an ADR of its
  own.
- **The cleansing trail becomes a storage or retention problem.** Its retention
  is currently unbounded, which is correct for reversibility and wrong for
  minimisation; reconciling the two is ADR-006's territory.
