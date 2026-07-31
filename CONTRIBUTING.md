# Contributing to EDPF

The engineering rulebook is
[Appendix Z](docs/standards/appendix-z-implementation-standards.md) and it is
normative — where anything here is shorter, Z wins.

## Day one

Follow [Z.22](docs/standards/appendix-z-implementation-standards.md#z22-day-one-onboarding-path).
Target: your first merged pull request within three days, ideally a test on a
low-criticality module.

```bash
dotnet build Edpf.slnx -c Release
```

```bash
dotnet test Edpf.slnx -c Release --filter "Category!=RequiresDocker"
```

Then run the walking skeleton
([instructions](docs/phases/p02-walking-skeleton/11-usage.md)) and its gate
demonstration. Break something deliberately and watch a test fail — a rule
you have not seen fail is a rule you do not yet trust.

## Before you write code

Identify the ADRs that constrain your change and name them in the PR. If your
change *makes* an architectural decision, it needs an
[ADR](docs/adr/README.md); if it makes a smaller one, a
[TDL entry](docs/tdl/README.md). Neither is optional, and neither is written
after the fact.

Apply **Principle 0** first: *if we build this, will we be better at it in
three years than the leading library's maintainers — and does that advantage
matter to a customer?* If either half is no, integrate a library and wrap it.

## Branching and commits

Trunk-based; feature branches live ≤ 3 days; `main` is always releasable.

```text
feature/<phase>-<slug>   fix/<issue>-<slug>   release/vX.Y   hotfix/<cve>-<slug>
```

Conventional Commits with a phase scope
([Z.5](docs/standards/appendix-z-implementation-standards.md#z5-git-standards)):

```text
feat(p08b): whitelist filter fields against entity metadata

Filter, sort and projection now resolve against IFieldMetadata rather than
reflection, so runtime-defined fields participate in the safety model.

Refs: FR-009, BR-008
ADR: ADR-030
```

Never put a secret, a customer name, or PHI in a commit message.

## The bar for a pull request

Fill in [the template](.github/PULL_REQUEST_TEMPLATE.md) honestly — an
unticked box with an explanation is far more useful than a ticked one without.

Your change must:

- Build clean on every target framework with warnings as errors.
- Carry tests that **fail if the code is wrong**, including a negative case,
  plus an isolation case for any new data path.
- Add XML documentation to every new public member.
- Contain no TODO, no placeholder, no hardcoded value, no `NotImplementedException`.
- Record a `PublicAPI.Unshipped.txt` entry for any public API change.
- State its threat-model and compliance-control impact, or say "none".
- Explain how it is rolled back.

Reviews follow [Z.6](docs/standards/appendix-z-implementation-standards.md#z6-code-review-checklist).
Reviewers: review the code, not the author; mark blocking comments as
blocking and prefix suggestions with `nit:`. Approving means you would be
comfortable being paged for it.

## Things that will get a change rejected

- Suppressing an analyzer inline instead of fixing the code, or weakening a
  rule instead of narrowing it.
- `#if` anywhere outside `Edpf.Compatibility`.
- Reading `DateTime.UtcNow` instead of `IClock`, or using `System.Random`
  anywhere near security.
- Touching `System.Security.Cryptography` directly instead of
  `ICryptoProvider`.
- A repository query that relies on the caller to pass a tenant filter.
- Any classified data — PHI, PII, secrets — in a log, trace, metric or error
  body.
- A test that asserts only by not throwing.

## Reporting a vulnerability

Do not open an issue. See [SECURITY.md](SECURITY.md).
