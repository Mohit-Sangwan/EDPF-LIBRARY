# Security policy

## Reporting a vulnerability

**Do not open a public issue, pull request, or discussion for a security
issue.** Report privately through GitHub's "Report a vulnerability" (Security
→ Advisories) on this repository, which opens a private advisory visible only
to the maintainers.

Please include: affected version or commit, affected component, reproduction
steps or proof of concept, and the impact you believe it has. If you have a
suggested fix, include it — but do not send a public pull request before the
advisory is resolved.

**Never include real patient data, production credentials, or any other
classified material in a report.** Synthetic reproduction data only. If a
finding can only be demonstrated with real data, say so and we will arrange a
secure channel.

## What to expect

| Stage | Target |
|---|---|
| Acknowledgement | 3 business days |
| Initial assessment and severity (CVSS v3.1) | 10 business days |
| Fix or documented mitigation — Critical / High | 30 days |
| Fix or documented mitigation — Medium / Low | next scheduled release |
| Advisory published, CVE requested where applicable | with the fix |

Reporters are credited in the advisory unless they ask not to be.

## Scope

**In scope:** the `src/` framework assemblies, `providers/`, and anything
those ship into a consuming application — in particular the tenant isolation
boundary, cryptography and key custody, the audit chain, authentication and
authorization, and the supply chain of released packages.

**Out of scope:** the walking skeleton in `samples/` and its
`gate-demonstration.ps1`. That sample is explicitly **non-shippable**, takes
documented shortcuts ([TDL-0001](docs/tdl/TDL-0001-skeleton-ensurecreated.md),
[-0002](docs/tdl/TDL-0002-outbox-log-transport.md),
[-0003](docs/tdl/TDL-0003-ephemeral-dev-keys.md)) including
ephemeral development keys and a `/dev/token` endpoint, and is never deployed.
Findings *about* the framework contracts it exercises are in scope; findings
about the sample's own shortcuts are already documented.

Also out of scope: issues requiring physical access, social engineering, or
compromise of the reporting user's own machine; and denial of service through
volumetric traffic against a deployment you do not own.

## Security posture of this project

Each release is validated against
[Z.19](docs/standards/appendix-z-implementation-standards.md#z19-security-validation-set):
SAST on every commit, dependency and secret scanning with a build-failing
policy, fuzz corpora on parsers, an adversarial cross-tenant isolation suite,
injection corpora across providers, an annual external penetration test, and
an external cryptographic design review before 1.0 and on any algorithm
change.

The threat model is public:
[18 STRIDE entries with their controls](docs/phases/p00-discovery-decisions/07-security.md).
Entries marked *proven* have an automated test that fails if the control
regresses.

## A note on compliance claims

EDPF ships *controls that enable* HIPAA, GDPR, SOC 2 and ISO 27001
compliance. It cannot make an application compliant, and no EDPF
documentation, marketing, or support statement may claim that it does.
Compliance is a property of the system, its processes, and its organization.
