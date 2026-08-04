# ADR-038 — Retire Tier 2 (net6.0): a support claim the repository could not back

- **Status:** Accepted
- **Date:** 2026-08-04
- **Amends:** [ADR-002](ADR-002-multi-target-strategy.md) (multi-target strategy)
- **Related:** [ADR-035](ADR-035-benchmark-gate-enforces-allocation.md) (a gate that
  never ran), [ADR-036](ADR-036-stored-form-must-equal-served-form.md) (a suite
  excluded by a filter), [ADR-037](ADR-037-v1-scope-boundary.md) (ship controls,
  not assertions)

## Context

ADR-002 defined three tiers. Tier 2 was net6.0 — "full surface minus newest
APIs" — and the ADR recorded an accepted risk against it:

> Accepted risk: net6.0 is out of Microsoft support; retained deliberately as
> the Tier 2 bridge and reviewed at Phase 32 (`CheckEolTargetFramework`
> disabled with this justification).

`Directory.Build.props` carried the matching comment: "its EOL status is
acknowledged and tracked as a Phase 32 review item."

**Phase 32 shipped in Wave 7 and the review was never performed.** Its
completion report contains no reference to net6.0, EOL, Tier 2 or target
frameworks. Two documents pointed at a follow-up that was never created — the
same shape as `IFieldMetadata.RequiredScope`, the benchmark baseline, and the
CI pipeline: a control whose existence is asserted by the thing that defers to
it.

The review was performed on 2026-08-04. net6.0 failed it on four independent
counts.

### 1. Out of Microsoft support, and the warning was suppressed

.NET 6 reached end of support on 2024-11-12 — **twenty-one months** before this
review. The SDK says so directly; re-arming the check produces:

```text
warning NETSDK1138: The target framework 'net6.0' is out of support and will
not receive security updates in the future.
```

NETSDK1138 carries a warning code, and `TreatWarningsAsErrors` is on. So the
suppression was not cosmetic — **it was the only thing keeping "builds clean
with warnings as errors" and "we ship net6.0" true at the same time.** Removing
one required removing the other.

For a framework whose value proposition is regulated-industry safety, offering
a runtime that receives no security patches is a contradiction, not a
convenience. A hospital that deploys EDPF on .NET 6 gets no BCL CVE fixes, and
EDPF's support claim is what told them it was fine.

### 2. Every dependency disclaimed it

All twenty `Microsoft.Extensions.*` and `System.*` packages at 9.0.18 emit:

> doesn't support net6.0 and has not been tested with it

**96 of the 96 warning lines in a full solution build were net6.0.** Not one
came from any other framework. These carry *no* warning code, which is why
`TreatWarningsAsErrors` never escalated them and a build with 48 distinct
warnings was described in the documentation as "building clean with warnings as
errors" — true of the compiler, false of the build.

EDPF was claiming support for a configuration its own dependency stack declared
untested.

### 3. Nothing had ever executed on it

Every test project targeted net10.0, except the Tier 3 suite which added net48.
Of five declared frameworks, tests had run on **two**.

That finding is broader than Tier 2 and is recorded here because Tier 2 is what
exposed it: `Edpf.Tier3Tests` was created on 2026-08-03 precisely because "a
declared framework and an executed one are different things" — and it was then
pointed at one tier and no further. net472 and net8.0 remained unexecuted for
another day, in the file that exists to prevent exactly that.

#### The cause was misdiagnosed twice before it was found

The first explanation was that `dotnet test` runs only one target framework of
a multi-targeted project, so each had to be named. **That is false**, and it
was written into the README, the CI workflow and this ADR's own working notes
before anyone ran the command to check. `dotnet test` cross-targets; a plain
run of the suite emits one result line per framework.

What was actually happening: `tests/Directory.Build.props` sets the *singular*
`<TargetFramework>` for every test project, and MSBuild resolves that in favour
of a project's *plural* `<TargetFrameworks>`, disabling cross-targeting with no
warning. Only one framework was ever **built**. A missing build output was read
as a limitation of the runner, and the wrong fix — name the frameworks — made
the symptom go away while leaving the cause in place.

It is worth being blunt about the shape of this, because it is the shape the
whole ADR is about: **the explanation was plausible, it was repeated in three
files, and it was never once tested.** The fix now clears the inherited
singular property, which is what makes the derived list below work at all.

### 4. It could not be executed

Adding net6.0 to the runtime suite fails at restore:

```text
error NU1701: Package 'xunit.runner.visualstudio 3.1.5' was restored using
'.NETFramework,Version=v4.6.1 … v4.8.1' instead of the project target
framework 'net6.0'.
```

Tier 2 was not *untested*. It was **untestable** with the pinned toolchain.
Testing it would have required a framework-conditional downgrade of the test
runner — a second toolchain, maintained indefinitely, so that one unsupported
framework could assert it still worked.

## Decision

**Tier 2 is retired. net6.0 is removed from every target framework list, and
`CheckEolTargetFramework` is armed.**

The tiers become:

| Tier | Frameworks | Surface |
| --- | --- | --- |
| 1 | net8.0, net10.0 | Full |
| 3 | net472, net48 | Reduced — data access, config, logging, security primitives |

Tier 3 keeps its number. Renumbering would break every `Tier 3` reference in
sixteen project files and five ADRs to save one integer, and the tier numbers
are labels rather than an ordering that must be dense.

**The runtime suite runs on every framework the libraries declare**, derived
from `$(EdpfLibraryTargetFrameworks)` rather than a hand-written list — in the
project file and in CI. The consolidation audit's finding applies directly: *a
rule that names its subjects goes stale; a rule that discovers them does not.*
A hand-kept list is what left three frameworks unexecuted.

### Arming the EOL check is the more important half

Removing net6.0 fixes today. `CheckEolTargetFramework=true` fixes the next one.

With it armed and warnings as errors on, **a framework reaching end of support
stops the build on the day it happens.** The response becomes a deliberate
decision — drop the tier, or knowingly retain it with a written justification —
taken at the moment of the event rather than discovered twenty-one months later
by someone reading a props file for another reason.

A clean build now *proves* the matrix contains nothing out of support. That is
a property, not a claim, and it is checkable by running the build.

**.NET 8 is the next to go**, and it is not far off. When that build breaks,
this ADR is the reason and the trigger below is the process.

## Consequences

### What this costs

- **Consumers on .NET 6 cannot use EDPF.** Today that set is empty — the
  product is `0.1.0-alpha.1`, unreleased, with no design partners signed and no
  published package. **The cost of this removal is zero now and rises
  permanently the moment v1.0 ships.** This was the last cheap moment to take
  it, which is the main argument for taking it now rather than at v1.0.
- **Sixteen project files** lost a "Tier 1 + Tier 2" comment. Mechanical.
- `EdpfPlatformTargetFrameworks` now holds the same value as
  `EdpfModernTargetFrameworks`. Both are kept: they encode different decisions
  — "deliberately outside Tier 3" and "a modern-only tool" — that coincide
  today and will diverge the next time a tier moves. Collapsing them would
  discard the distinction to remove a duplicate string.

### What it buys

- 48 build warnings → **0**.
- Four declared frameworks, four executing tests, 11/11 identical on each.
- A support matrix that contains only runtimes receiving security updates, and
  a build that fails if that stops being true.

### What it does not decide

Whether ADR-002's remaining tiering is right at all. Tier 3 still costs — seven
portable equivalents and counting — and ADR-002's own revisit trigger governs
it. Retiring Tier 2 says nothing about Tier 3 except that it is now tested on
both its frameworks rather than one.

## Revisit triggers

- **A design partner requires net6.0.** Then this is reopened with a named
  customer, and the security posture of shipping an unsupported runtime is
  their decision to accept in writing, not an assumption in a props file.
- **The build fails on NETSDK1138.** That is the check working. A tier is
  dropped or knowingly retained, in an ADR, that day.
- **A future test runner supports a framework the current one cannot.** The
  fourth finding above was toolchain-specific, and toolchains move.
