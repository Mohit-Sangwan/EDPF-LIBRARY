# ADR-035 — The benchmark gate enforces allocation always, and time when the measurement supports it

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 31 (revisited) — benchmark regression gate, Z.9 / EDPF-BNC-001
- **Related:** [ADR-008](ADR-008-test-strategy.md) (test strategy), Z.9, Z.12 (a claimed capability is a tested capability)

## Context

Z.9 specifies a benchmark regression gate: a run more than **5%** slower or
more allocating than the recorded baseline fails the build. Phase 31 built
`BenchmarkBaseline`, the comparison logic, and it is correct.

It had never been run. **No baseline had ever been captured, and no tooling
existed to capture one** — so the gate had nothing to compare against and could
not fire. A control that cannot fire is indistinguishable from an absent one,
which is the same shape as the `RequiredScope` defect Phase 08b found.

Running the benchmarks for the first time produced the measurements the gate
needs, and one number that changes how it should be operated.

**A benchmark reports two different numbers, and a gate needs the one
BenchmarkDotNet does not print in the summary table.**

*Within-run precision* — the confidence margin — describes how tightly one
run's samples cluster. On a full job here it is excellent: 0.7% to 1.9%.

*Between-run reproducibility* is what the gate actually depends on, because it
compares today's run against a baseline captured days ago. Two consecutive full
jobs on this machine, no code change between them:

| Benchmark | Run A | Run B | Change | Run B's own margin |
| --- | ---: | ---: | ---: | ---: |
| `SerializeRoundTrip[64 KB]` | 9,969.5 ns | 14,811.5 ns | **+48.6%** | 1.9% |
| `DeserializeEnvelope[1 KB]` | 107.0 ns | 150.5 ns | **+40.6%** | 0.8% |
| `EncryptField[32 B]` | 898.8 ns | 1,250.1 ns | **+39.1%** | 0.7% |
| `SerializeRoundTrip[32 B]` | 140.8 ns | 109.0 ns | **−22.6%** | 0.9% |

Every benchmark moved by more than 22% while every run called itself precise to
under 2%. **The two statistics differ by roughly a factor of thirty.**

### The rejected design, run

An earlier draft of this ADR proposed calibrating enforcement per benchmark
from the confidence margin: enforce timing where the benchmark's own margin
sits below the tolerance, advise where it does not.

**That design was implemented and executed against the freshly captured
baseline, with no code change between capture and comparison. It failed the
build:**

```
REGRESSION  SerializeRoundTrip[PlaintextBytes=32]: mean 109.01 ns -> 116.13 ns (6.5%).
EXIT=1
```

The baseline recorded that benchmark's margin as 0.9%, so the calibration
judged it precise enough to enforce. It then drifted 6.5% on the next run of
identical code. Every one of the nine allocation figures matched exactly in the
same run.

This is the whole argument in one output line: a gate built on within-run
precision fails on drift, gets diagnosed as flaky, and is switched off.

**Allocation is different in kind, not degree.** BenchmarkDotNet *counts*
allocated bytes rather than sampling them. Across those same two runs every
figure was byte-identical — 136, 240, 392, 1,128, 1,232, 2,376, 65,642, 65,744,
131,400 — with no confidence interval, because there is no sampling.

## Decision

**Allocation is always enforced. A timing regression is enforced when that
benchmark's own recorded noise is below the tolerance, and reported as advisory
when it is not.**

1. **Allocation regressions fail the build.** The measurement is deterministic,
   so a 5% tolerance means what it says. Allocation is also the dimension that
   catches the defects worth catching — a boxing conversion added to a hot
   path, a `ToList()` inside a loop, an accidental closure capture — all of
   which show up as bytes long before they show up as milliseconds.

2. **Timing regressions are advisory** on any machine whose between-run drift
   nobody has measured — which is every developer workstation and most shared
   CI runners.

3. **The confidence margin is recorded but is explicitly not the enforcement
   criterion.** It is useful context and it answers the wrong question; the
   script says so at the decision point, so the next person to look does not
   repeat the mistake.

4. **A baseline must come from a full job.** `-Short` exists for smoke-testing
   the plumbing and warns loudly; margins there run 29%–273%.

5. **`-EnforceTiming` is for a dedicated runner whose between-run drift has
   been measured** — by capturing several baselines and comparing them, which
   is the only way to know. Switching it on because the margins look small is
   the exact error this ADR records.

6. **Benchmark identity includes its parameters.** `EncryptField` at 32 bytes
   and at 64 KB are different benchmarks that happen to share a method name;
   collapsing them would compare a small-payload run against a large-payload
   baseline and report a 2,700% regression.

## Consequences

### Accepted costs

- **Timing regressions go unenforced on ordinary hardware.** A genuine 10%
  slowdown prints as an advisory and can be scrolled past. That is a real gap,
  and it is preferable to a gate that fails on drift and gets switched off
  within a fortnight.
- **Between-run drift is itself variable.** One pair of runs moved every
  benchmark by 22%–49%; another moved eight of nine by under 5% and one by
  6.5%. So a single quiet comparison proves nothing about the next one, and
  "it passed yesterday" is not evidence the timing dimension is gateable here.
- **The baseline is machine-specific and must be recaptured per runner.** A
  baseline from one machine gates nothing on another. The file records the
  machine and processor count so a mismatch is visible rather than silent.
- **Neither dimension catches an algorithmic regression that allocates nothing
  and is too fast to measure precisely** — an O(n²) loop over a stack-allocated
  span, say. Real, and not covered.

### What this does not claim

- **No performance target is asserted.** Z.9 and Golden Rule 4 require numbers
  rather than adjectives; these are measurements of what the code does today on
  one machine, not a promise about what it will do on a server. Nothing here
  says "fast".
- **The captured baseline is not a CI baseline.** It was taken on a developer
  workstation with other processes running. It makes the gate *operable* and
  demonstrates the tooling end to end; it does not make it *authoritative*.

## Revisit triggers

- **A dedicated benchmark runner is provisioned.** Measure its between-run
  drift by capturing several baselines and diffing them; if that drift is below
  5%, switch on `-EnforceTiming` and this ADR's central compromise disappears.
- **Allocation figures start varying between runs.** That would mean the
  determinism the enforced half rests on has stopped holding, and the whole
  arrangement needs rethinking.
- **Someone proposes enforcing timing because the confidence margins look
  small.** That is the specific error recorded above, and the answer is to
  measure between-run drift instead.
- **A benchmark is added whose cost is time-only** — no allocation to watch —
  because the enforced half of the gate would then be blind to it.
- **Allocation figures start varying between runs.** That would mean the
  determinism this decision rests on has stopped holding, and the whole
  arrangement needs rethinking.
