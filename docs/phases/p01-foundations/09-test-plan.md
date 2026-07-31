# Phase 01 — Test plan

## Unit tests (`tests/Edpf.UnitTests`, 59 tests)

Every shared-kernel primitive, per Z.7 (`Method_Scenario_ExpectedResult`, AAA,
no wall-clock, no I/O):

- `Result` / `Result<T>`: success, failure, value-on-failure throws,
  `TryGetValue`, `Map`, `Bind` short-circuit, `Match` both branches, implicit
  conversions.
- `Error`: equality, correlation enrichment, safe `ToString`.
- `EntityId<T>`: brand distinctness at the type level, empty semantics.
- `PageRequest` / `PagedResult<T>`: boundaries (0, 1, max, max+1), default
  instance semantics, page arithmetic at zero and exact multiples.
- `Guard`: null, empty, whitespace, struct default.
- `TenantContextAccessor` / `CorrelationContextAccessor`: scope push/pop,
  nesting, and **no leakage across parallel async flows**.
- `EncryptionEnvelope`: wire-format round-trip, fixed 35-byte header, empty
  ciphertext, truncated buffer, unknown version, nonce/tag length rejection.
- `KeyHandle`: dispose zeroes material and invalidates the handle.

## Architecture tests (`tests/Edpf.ArchitectureTests`, 12 tests)

Clean Architecture and the Z.4 rules as executable rules:

| Test | Rule |
|---|---|
| `Abstractions_ReferencedAssemblies_AreBclOnly` | EDPF0001 |
| `SrcSources_OutsideCompatibility_ContainNoConditionalCompilation` | EDPF0002 |
| `SrcAndSampleSources_OutsideCompatibility_ContainNoSystemTimeReads` | EDPF0003 |
| `SecuritySources_UseNoInsecureRandom` | EDPF0004 |
| `SrcAssemblies_AsyncPublicMethods_AcceptCancellationToken` | EDPF0007 |
| `KernelSources_DoNotTouchCryptographyDirectly` | Z.10 |
| `Core_DoesNotReference_ProvidersOrEfCore` | layering |
| `Core_PublicClasses_AreSealedOrAbstractOrStatic` | Z.3 rule 7 |
| `Abstractions_Types_ResideInMirroredNamespaces` | Z.2 |
| `EncryptionEnvelope_MatchesDiagram125` | C4 §12.5 conformance |
| `AuditWriter_MatchesDiagram123` | C4 §12.3 conformance |
| `PipelineStages_CanonicalOrder_MatchesAdr012` | ADR-012 |

## Negative verification

Phase 01 §⑤ requires verifying *the test*, not just the build: an
architecture test that never fails is decoration. Each rule above was
confirmed to fail when deliberately violated during authoring — the source
scans initially flagged XML-doc references to `DateTime.UtcNow` and
`System.Security.Cryptography`, which is exactly the detection working; the
rules were then narrowed to code lines (`CodeMatches`) rather than weakened.

Re-verify on any change to `SourceRuleTests`: introduce a violation, confirm
red, revert.

## Coverage

Line ≥ 80 %, branch ≥ 70 % on core assemblies (Z.7). Mutation ≥ 60 % is
scheduled nightly from Phase 32; coverage without mutation score is a vanity
metric.
