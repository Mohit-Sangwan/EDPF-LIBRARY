# ADR-007: Crypto-agility — algorithm registry + versioned envelopes

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Security Architect, Chief Architect

## Context

Health data carries a 20-year retention horizon; today's algorithms will not
survive it (Gap #22). Post-quantum standards (FIPS 203/204/205 — ML-KEM,
ML-DSA, SLH-DSA) are final; the question is whether migrating to them ever
requires touching stored data.

## Options considered

1. **Fix AES-256-GCM everywhere, migrate later.** "Later" means a petabyte
   re-encryption estate-wide — exactly the outcome to design away.
2. **Per-deployment algorithm configuration without ciphertext metadata.**
   Decryption becomes guesswork the moment config changes; disaster during
   partial rollouts.
3. **Algorithm registry + self-describing versioned envelopes.** Every
   ciphertext carries `{version, algId, keyId, keyVersion}` in a fixed wire
   format (C4 §12.5: 35-byte little-endian header + nonce + ciphertext +
   tag). Encrypt always uses the registry's current algorithm; decrypt always
   resolves what the envelope declares.

## Decision

Option 3, implemented in `Edpf.Abstractions.Security.EncryptionEnvelope`
(wire format asserted by unit + diagram-conformance tests) and the skeleton's
`AlgorithmRegistry`. AES-256-GCM is algorithm id 1. Adding ML-KEM or any
successor is `Register(id, algorithm)` plus configuration: existing data
continues to decrypt under its original parameters; new data uses the new
algorithm; optional lazy re-encryption on read migrates the estate over time.
**No schema change, no data migration, no downtime.** Algorithm ids are
stable forever; re-registering an id throws.

## Consequences

- Positive: PQC migration is an operational rollout, not a data project;
  hybrid schemes register as their own ids.
- Negative: 35 bytes of envelope overhead per ciphertext — accepted, and
  measured in the Z.18 `encrypt/decrypt per field size` benchmark.
- Accepted risk: registry misconfiguration; an unknown id is a hard
  EDPF-SEC-5001 failure, never a fallback.

## Revisit trigger

NIST deprecates an algorithm in the registry (add successor id, schedule lazy
re-encryption); or envelope overhead breaches the field-encryption benchmark
budget.
