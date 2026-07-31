# Phase 01 — Contracts added to `Edpf.Abstractions`

Zero package references (rule EDPF0001). Conventions binding on every
interface (§10.1): async methods return `Task<Result<T>>` and end `Async`;
every async public method accepts a `CancellationToken`; no interface exposes
a provider-specific type; nullable reference types enabled; breaking changes
require an ADR and a `PublicAPI.Unshipped.txt` edit.

## Primitives

| Type | Purpose |
|---|---|
| `Result`, `Result<T>` | Expected failure without exception-as-control-flow (Z.3 rule 8) |
| `Error`, `ErrorCategory`, `ErrorCodes` | The §10.2 taxonomy; codes stable forever |
| `EntityId<T>` | Strongly-typed identifiers |
| `PageRequest`, `PagedResult<T>` | One pagination contract, bounded page size |
| `IClock` | The only sanctioned time source (rule EDPF0003) |
| `ICorrelationContext`, `ICorrelationContextAccessor` | Ambient correlation/request/causation ids |
| `DataClassificationLevel`, `DataClassificationAttribute` | Machine-discoverable PII/PHI tagging |

## Tenancy (the Phase 12 seam)

`ITenantContext` · `TenantDescriptor` · `ITenantResolver` ·
`ITenantContextAccessor` · `ITenantStore` · `TenantIsolationMode`
(SharedSchema | SchemaPerTenant | DatabasePerTenant).

Declared now, implemented in Phase 12, so no layer below is written
tenant-blind or region-blind (`Region` is on the context from day one per
ADR-010).

## Security (the Phase 20 seam)

`ICryptoProvider` · `IKeyManagementService` · `IAlgorithmRegistry` ·
`ISymmetricAlgorithm` · `IHashingService` · `ITokenizer` ·
`EncryptionEnvelope` · `KeyScope` · `KeyHandle`.

`EncryptionEnvelope` is concrete rather than an interface deliberately: it is
a wire format fixed by ADR-007 (C4 §12.5), and a wire format with a
substitutable implementation is not a wire format.

## Audit, data, consistency

`IAuditWriter` · `AuditEventDescriptor` · `IAuditChainVerifier` ·
`AuditChainVerification` · `IRepository<TEntity,TKey>` ·
`IReadRepository<TEntity,TKey>` · `IUnitOfWork` · `ITenantScopedEntity` ·
`IAuditableEntity` · `IOutboxDispatcher` · `IIdempotencyStore` ·
`IdempotencyRecord`.

## Public API baseline

The full surface is captured in
[`PublicAPI.Unshipped.txt`](../../../src/Edpf.Abstractions/PublicAPI.Unshipped.txt)
(249 entries). Any addition, removal or signature change requires editing
that file in the same commit — CI fails otherwise (EDPF0013). At the G1 gate
the unshipped entries are promoted to `PublicAPI.Shipped.txt` and the surface
becomes SemVer-governed (Z.14).
