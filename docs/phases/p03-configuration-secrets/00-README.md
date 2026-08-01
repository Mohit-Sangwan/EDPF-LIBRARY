# Phase 03 — Configuration & Secrets

**Status:** Complete · **Squad:** C · **Depends on:** Phases 01, 02

## Purpose

A configuration system that reads from every enterprise source, validates at
startup rather than at first use, hot-reloads safely, and never — under any
circumstance — writes a secret to a log, an exception message, or a
diagnostic dump.

## Constraining ADRs

ADR-002 (TFM tiers) · ADR-009 (package boundaries).
**Produces** [ADR-013](../../adr/ADR-013-configuration-precedence.md).

## Scope

**In:** providers, options binding, validation, hot reload, secret-store
integration, rotation.
**Out:** feature flags (Phase 28 — related, but a different lifecycle and a
different audience).

## Deliverables

| Deliverable | Location |
|---|---|
| `ISecretStore`, `SecretRotationView` | `src/Edpf.Abstractions/Configuration/` |
| `ISecretRotationHandler`, `SecretRotationEvent` | `src/Edpf.Abstractions/Configuration/` |
| `IConfigurationValidator<T>` | `src/Edpf.Abstractions/Configuration/` |
| `SecretValue` — redacting, non-serializable, zeroizing | `src/Edpf.Abstractions/Configuration/SecretValue.cs` |
| Precedence, as code | `src/Edpf.Configuration/EdpfConfigurationPrecedence.cs` |
| Startup validation bridge | `src/Edpf.Configuration/Validation/` |
| Transactional hot reload with last-known-good | `src/Edpf.Configuration/Reload/ValidatedOptionsMonitor.cs` |
| In-memory, environment and chained stores | `src/Edpf.Configuration/Secrets/` |
| Rotation coordinator (dual-secret window) | `src/Edpf.Configuration/Secrets/SecretRotationCoordinator.cs` |
| Secret-store conformance suite | `tests/Edpf.UnitTests/Configuration/SecretStoreConformanceTests.cs` |

## The precedence (ADR-013)

Lowest to highest: built-in defaults → `appsettings.json` →
`appsettings.{Environment}.json` → XML (Tier 3 legacy) → user secrets (dev
only) → environment variables → command line → **secret store**. The order is
asserted against the ADR by an architecture test.

## Exit criteria — status

- [x] Precedence declared, encoded and test-enforced.
- [x] Zero secrets in any log, exception, `ToString()`, interpolation or
      serialization under adversarial test — see
      [09-test-plan.md](09-test-plan.md).
- [x] Hot reload proven transactional: an invalid snapshot is rejected, the
      last-known-good is retained, listeners are not notified, and the stale
      state is observable for alerting.
- [x] Credential rotation proven with a dual-secret overlap and handler
      notification, including the failure path that keeps the overlap open.
- [x] Secret stores pass an identical conformance suite.
- [ ] **Cloud backends (Key Vault, AWS Secrets Manager, Vault) — deferred**,
      see [13-extension-points.md](13-extension-points.md).
