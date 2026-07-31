# TDL-0003: Dev harness generates ephemeral keys when unconfigured

**Phase:** p02 · **Status:** Accepted · **Constraining ADRs:** ADR-006, ADR-007

The skeleton needs a JWT signing key and a master wrapping key. Committing
either to configuration would put key material in source control (Z.10);
demanding manual setup would break "clone, run it" onboarding (Z.22 item 6).

Decision: when unconfigured **in Development**, both keys are generated
per-process (`RandomNumberGenerator`); tokens and wrapped KEKs die with the
process. In any other environment a missing signing key fails startup with
EDPF-CFG-8001 — fail at startup, never mid-request. Real custody
(`ISecretStore`, HSM/KMS-backed) is Phase 03/20 scope.

Consequence accepted: a Development database outlives its process keys and
becomes undecryptable across restarts — harmless for a throwaway harness,
and a useful daily reminder that key loss equals data loss (ADR-006 by
accident).
