# ADR-013: Configuration precedence & reload semantics

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Squad C lead

## Context

EDPF is consumed by hosts ranging from containerised .NET 10 services to
net48 WebForms applications on hospital hardware. Each has its own idea of
where configuration comes from. Without a declared precedence, "why is this
value not taking effect?" becomes unanswerable, and — worse — silent partial
reload becomes a production-incident generator.

## Options considered

1. **Host decides.** Each application assembles its own configuration stack.
   Maximum flexibility; every support call starts from first principles, and
   two deployments of the same product behave differently.
2. **Single source (environment only).** Twelve-factor purity; unusable for
   the Tier 3 brownfield hosts that read `web.config`, and hostile to
   operators who expect a file they can inspect.
3. **One declared precedence, applied by the framework**, with reload
   semantics stated per option.

## Decision

Option 3. Precedence, lowest to highest:

```text
built-in defaults
  → appsettings.json
  → appsettings.{Environment}.json
  → XML (web.config / app.config, Tier 3 legacy hosts)
  → user secrets (development only)
  → environment variables
  → command line
  → secret store (Key Vault / AWS Secrets Manager / Vault)
```

The secret store sits highest and is the **only** source trusted with
credentials. The order is code — `EdpfConfigurationPrecedence.Order` — and an
architecture test asserts it against this ADR, so changing precedence
requires superseding the decision.

**Reload semantics are explicit per option, and reload is transactional.**
A candidate snapshot is validated in full before adoption; a snapshot that
fails validation is rejected, the last-known-good is retained, and an alert is
raised (`ValidatedOptionsMonitor<T>`, `IsServingStaleConfiguration`). Startup
validation uses `ValidateOnStart` semantics: misconfiguration fails the boot,
not the first request that happens to touch it.

**Secrets never appear as values.** `SecretValue` renders as `***` through
`ToString()` and interpolation, refuses serialization, hashes by length, and
zeroes its buffer on dispose. Rotation uses a dual-secret overlap window so
credentials roll without downtime; the rotation is audited by key, store and
time — never by value.

## Consequences

- Positive: one precedence to teach and to debug; a bad configuration push is
  a loud no-op instead of a half-applied outage.
- Negative: the highest-precedence source being remote (a vault) means
  startup depends on it — mitigated by the chained store, which lets a
  deployment layer a local fallback deliberately.
- Accepted risk: `SecretValue.Reveal()` still exists and can be misused; it is
  conspicuous at call sites and reviewable, which is the most a library can do.

## Revisit trigger

An operator demonstrates a legitimate case where a lower-precedence source
must override a higher one; or startup latency against a remote vault
breaches the cold-start NFR.
