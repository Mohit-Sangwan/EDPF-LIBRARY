# ADR-035 — The benchmark gate enforces allocation and advises on time

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

**On a developer machine, the 95% confidence margin ranged from 29% to 273% of
the mean.**

| Benchmark | Mean | Margin | Margin as % of mean |
| --- | ---: | ---: | ---: |
| `DeserializeEnvelope[32 B]` | 41.0 ns | ±112.2 ns | 273% |
| `SerializeRoundTrip[1 KB]` | 268.8 ns | ±580.9 ns | 216% |
| `DeserializeEnvelope[64 KB]` | 5,966.8 ns | ±8,227.1 ns | 138% |
| `EncryptField[64 KB]` | 17,564.5 ns | ±13,704.2 ns | 78% |
| `DeserializeEnvelope[1 KB]` | 128.2 ns | ±37.2 ns | 29% |

A 5% tolerance applied to a measurement uncertain to ±29% at best does not
detect regressions. It reports them at random — and **a gate that cries wolf is
a gate somebody disables**, which leaves the codebase worse off than having no
gate and knowing it.

**Allocation is different in kind, not degree.** BenchmarkDotNet *counts*
allocated bytes rather than sampling them. The figures repeat exactly:
136 B, 1,128 B, 65,642 B. There is no confidence interval because there is no
sampling.

## Decision

**The gate enforces allocation at 5% and reports timing as advisory, unless
run on hardware someone has declared quiet.**

1. **Allocation regressions fail the build.** The measurement is deterministic,
   so a 5% tolerance means what it says. Allocation is also the dimension that
   catches the defects worth catching — a boxing conversion added to a hot
   path, a `ToList()` inside a loop, an accidental closure capture — all of
   which show up as bytes long before they show up as milliseconds.

2. **Timing regressions print as advisories by default.** They are worth
   seeing; they are not worth failing a build on, from a machine whose noise
   floor is six times the tolerance.

3. **`-EnforceTiming` exists for a dedicated runner.** Fixed hardware, no
   other load, full BenchmarkDotNet job. On that machine the 5% timing gate is
   meaningful and should be switched on.

4. **The captured baseline records its own noise.** `worstMarginFraction` sits
   in the baseline file next to the measurements, and capture warns when it
   exceeds the tolerance. A reader can therefore see how much of the 5% budget
   is measurement error before deciding whether to believe a timing verdict.

5. **Benchmark identity includes its parameters.** `EncryptField` at 32 bytes
   and at 64 KB are different benchmarks that happen to share a method name;
   collapsing them would compare a small-payload run against a large-payload
   baseline and report a 2,700% regression.

## Consequences

### Accepted costs

- **Timing regressions can land undetected** on ordinary developer hardware
  until someone runs the gate somewhere quiet. That is a real gap, and it is
  preferable to a red build that everybody has learned to ignore.
- **The baseline is machine-specific and must be recaptured per runner.** A
  baseline from one machine gates nothing on another. The file records the
  machine and processor count so a mismatch is visible rather than silent.
- **Allocation-only enforcement misses algorithmic regressions that allocate
  nothing** — an O(n²) loop over a stack-allocated span, say. Real, and not
  covered.

### What this does not claim

- **No performance target is asserted.** Z.9 and Golden Rule 4 require numbers
  rather than adjectives; these are measurements of what the code does today on
  one machine, not a promise about what it will do on a server. Nothing here
  says "fast".
- **The captured baseline is not a CI baseline.** It was taken on a developer
  workstation with other processes running. It makes the gate *operable* and
  demonstrates the tooling end to end; it does not make it *authoritative*.

## Revisit triggers

- **A dedicated benchmark runner is provisioned.** Recapture there, switch on
  `-EnforceTiming`, and this ADR's central compromise disappears.
- **Timing advisories are routinely ignored.** Then they are noise in a
  different form and should either be enforced properly or removed.
- **A benchmark is added whose cost is time-only** — no allocation to watch —
  because the enforced half of the gate would then be blind to it.
- **Allocation figures start varying between runs.** That would mean the
  determinism this decision rests on has stopped holding, and the whole
  arrangement needs rethinking.
