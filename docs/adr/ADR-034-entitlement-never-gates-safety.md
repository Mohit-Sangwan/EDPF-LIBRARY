# ADR-034 — Entitlement gates features, never care; offline means expiry is the only revocation

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 34b — Enterprise Installers (offline entitlement validation)
- **Related:** [ADR-002](ADR-002-multi-target-strategy.md) (seventh occurrence), [ADR-009](ADR-009-licensing.md), [ADR-021](ADR-021-zero-downtime-migration.md), Z.10 (crypto confinement)

## Context

On-premises and air-gapped hospital deployments need entitlement validation
that works with no network. Two problems follow, and the second is not a
technical one.

**Offline means no revocation.** An air-gapped server cannot ask whether a
licence was withdrawn, and it cannot check its own clock against an authority.
Both facts are permanent properties of the deployment, not gaps to be
engineered away.

**A licence check in a hospital can become a patient-safety hazard.** This is
the part that deserves stating plainly. A commercial control that can stop a
clinician opening a chart — because a licence lapsed over a bank holiday,
because a clock drifted, because a renewal email went to someone who left — is
a hazard introduced into a clinical environment *by the vendor*, for the
vendor's benefit. It is not a hazard the hospital chose or can meaningfully
mitigate.

The specification already gestures at this: *"a disabled module must be
invisible, not error-producing."* This ADR takes it further, because
"invisible" is about user experience and the harder question is about scope.

## Decision

**1. Entitlement gates features. It never gates reading data that already
exists, writing audit, break-glass, or a subject-access export.**

`ModuleGate.Register` **throws** when handed one of those capabilities. The
constraint is structural, not documented guidance: the code that would express
the hazard does not compile into a working configuration.

Each of the four is something a clinician or an investigator may need at a
moment when the commercial relationship is the least important fact in the
room. Subject-access is on the list because a data subject's right under GDPR
Art. 15 does not lapse when the controller's licence does.

**2. An invalid entitlement degrades the system; it does not stop it.**
Gateable modules switch off, everything else keeps running, and `Apply`
reports the failure to whoever is monitoring rather than to whoever is
treating a patient.

**3. Never-gateable capabilities are available when no entitlement has been
applied at all** — the state during startup, and after a licence file has been
deleted.

**4. Entitlements are short-lived, because expiry is the only revocation an
offline deployment has.** A three-year entitlement is a three-year window in
which withdrawal is impossible. Quarterly re-issue is the trade: more
operational friction, and a revocation that actually takes effect.

**5. Clock rollback is treated as tampering, not drift.** The verifier
compares against the highest time it has ever observed. Winding an air-gapped
machine's clock back would otherwise revive an expired entitlement
indefinitely.

**6. Signature verification is checked first, and a forgery reports only
that.** A tampered entitlement's dates and deployment id mean nothing;
reporting "expired" for a forged licence would tell an attacker which field to
edit next.

**7. Cryptography stays in `Edpf.Security`.** Licensing consumes
`IDetachedSignatureVerifier`. Z.10 confines crypto to one reviewed assembly,
and the rule's own comment says a second exemption should be a deliberate
architectural decision — so the seam was built instead of the exemption taken.

The interface offers **verification only, with no signing counterpart**. A
symmetric implementation would put a shared secret on the customer's
air-gapped server, which is a licence-minting kit.

## Consequences

### Accepted costs

- **Entitlement enforcement is weaker than it could be.** A determined
  customer can patch the check out. That is true of every client-side licence
  and pretending otherwise leads to measures — dongles, phone-home, kill
  switches — whose failure modes are far worse in a hospital than the revenue
  they protect.
- **Short-lived entitlements mean recurring operational work**, including for
  air-gapped sites where delivery is a person carrying a file. That work is
  the price of revocation existing at all.
- **The high-water mark depends on storage the operator could reset.** This
  type cannot enforce where it lives; a deployment that stores it in a
  world-writable file has no rollback defence. Stated rather than implied.
- **Entitlement verification is Tier 1 + Tier 2 only** (ADR-002, seventh
  occurrence). `RSA.ImportSubjectPublicKeyInfo` and RSASSA-PSS are unavailable
  on .NET Framework's default RSA implementation. Resolved by tiering the
  *file* in the project rather than by an `#if`, which EDPF0002 forbids
  outside `Edpf.Compatibility`.

### What this does not claim

- **No installer.** MSI, MSIX, Linux packages, silent-install parameters and
  upgrade orchestration are not here. Building an installer that has never
  installed anything would be a claim rather than a capability (Z.12).
- **No entitlement issuance.** The private key and the issuing process belong
  to whoever sells the software, and neither is in this repository.
- **No entitlement file format or transport.** The canonical byte encoding is
  defined and signed; how it is packaged, delivered and stored is a deployment
  concern.
- **No proof of tamper-resistance.** Client-side enforcement is
  defence-in-depth against accident and casual misuse, not against a motivated
  attacker with the binary.

## Revisit triggers

- **Anyone proposes gating a fifth capability that touches care delivery.**
  The list is short deliberately; each addition should require the argument to
  be made again, in writing.
- **A deployment asks for entitlements measured in years.** That is a request
  to remove revocation, and it should be answered as such.
- **Phone-home or hard-stop enforcement is proposed.** Both change this
  decision's safety posture and neither should arrive as an implementation
  detail.
- **Tier 3 needs entitlement verification.** Today it does not — entitlements
  are checked by servers and installers, not by the device hosts that justify
  Tier 3 (Phase 24f). If that changes, the padding and key-import choices need
  revisiting together.
