# Phase 34b — Enterprise Installers: offline entitlement validation

**Status:** Entitlement validation and module gating complete; installer packaging deferred
**Gate contribution:** G8 (Adoptable)
**ADR produced:** [ADR-034 — Entitlement gates features, never care](../adr/ADR-034-entitlement-never-gates-safety.md)

## What this delivers

| File | Contents |
| --- | --- |
| [`Entitlement.cs`](../../src/Edpf.Licensing/Entitlement.cs) | The entitlement, its canonical signed form, and the check outcome |
| [`ModuleGate.cs`](../../src/Edpf.Licensing/ModuleGate.cs) | Offline verification with clock-rollback resistance, and gating that cannot reach care |
| [`RsaSignatureVerifier.cs`](../../src/Edpf.Security/RsaSignatureVerifier.cs) | RSASSA-PSS verification, in the one assembly Z.10 lets crypto live in |

## The decision that matters

**A licence check in a hospital can become a patient-safety hazard**, and that
deserves stating plainly rather than being discovered.

A commercial control that can stop a clinician opening a chart — because a
licence lapsed over a bank holiday, because a clock drifted, because a renewal
email went to someone who left — is a hazard introduced into a clinical
environment *by the vendor, for the vendor's benefit*. It is not one the
hospital chose or can meaningfully mitigate.

So `ModuleGate.Register` **throws** when handed a never-gateable capability:

| Capability | Why it can never be licensed off |
| --- | --- |
| `core.read` | Reading a record that already exists |
| `core.audit.write` | An audit that stops on a billing dispute is not an audit |
| `core.breakglass` | The emergency path, needed precisely when things are wrong |
| `core.export.subjectaccess` | A GDPR Art. 15 right does not lapse when the controller's licence does |

The constraint is **structural, not documented guidance** — the configuration
that expresses the hazard cannot be built. And these stay available when *no*
entitlement has been applied at all, which is the state during startup and
after a licence file has been deleted.

An invalid entitlement therefore **degrades** the system rather than stopping
it: gateable modules switch off, everything else keeps running, and the failure
is reported to whoever is monitoring rather than to whoever is treating a
patient.

## Offline means no revocation

An air-gapped server cannot ask whether a licence was withdrawn, and cannot
check its own clock against an authority. Both are permanent properties of the
deployment.

- **Expiry is the only revocation mechanism that exists**, so entitlements are
  short-lived and re-issued. A three-year licence is a three-year window in
  which withdrawal is impossible.
- **Clock rollback is treated as tampering, not drift.** The verifier compares
  against the highest time it has ever observed; winding the clock back would
  otherwise revive an expired entitlement indefinitely.
- **Signature is checked first, and a forgery reports only that.** A tampered
  entitlement's dates mean nothing, and reporting "expired" for a forgery would
  tell an attacker which field to edit next.

The canonical signed form is built by hand — field-tagged, length-prefixed,
sorted, invariant-culture — rather than by serialising the object. A
serializer's field ordering and culture handling are free to change underneath
a signature, and a signature that stops verifying after a library upgrade is
indistinguishable from one that was tampered with.

## Two architecture rules worked

**Z.10 (crypto confinement) held without a second exemption.** The rule's own
comment says adding one "should be a deliberate architectural decision", so I
built the seam instead: `IDetachedSignatureVerifier` in Abstractions, the RSA
implementation in `Edpf.Security`, and licensing consuming the interface. The
interface offers **verification only** — a signing counterpart would invite a
symmetric implementation, and a shared secret on a customer's air-gapped server
is a licence-minting kit.

**ADR-002 bit twice more**, making seven occurrences:

- `ReadOnlySpan<byte>` in the Abstractions interface needs `System.Memory`, and
  that assembly carries **zero** package references (EDPF0001). Changed to
  `byte[]`; an entitlement is verified at startup, not in a loop.
- `RSA.ImportSubjectPublicKeyInfo` and RSASSA-PSS are unavailable on .NET
  Framework's default RSA. Resolved by **tiering the file** in the project
  rather than by an `#if`, which EDPF0002 forbids outside `Edpf.Compatibility`.
  Dropping to PKCS#1 v1.5 was rejected: this format is being *defined* here,
  not interoperated with, so there is no legacy verifier to accommodate.

  That is also the right answer on its merits — entitlements are checked by
  servers and installers, not by the WinForms device hosts that justify Tier 3
  (Phase 24f).

## Exit criteria

| Criterion | Status |
| --- | --- |
| Offline / air-gapped entitlement validation | Met |
| Module entitlement gating features and API surface | Met |
| Graceful degradation — a disabled module is invisible, not error-producing | Met |
| Entitlement changes reportable for audit | Met — `EntitlementCheck` carries status and reason |
| MSI / MSIX / Linux packaging, silent install, upgrade orchestration | **Deferred** |

## Deferred, with reasons

**The installers themselves.** MSI, MSIX, Linux packages, silent-install
parameters and upgrade orchestration are not here. An installer that has never
installed anything is a claim rather than a capability (Z.12), and verifying
one needs target machines rather than more code.

**Entitlement issuance.** The private key and the issuing process belong to
whoever sells the software. Neither is in this repository, and the verification
interface deliberately has no signing counterpart.

**File format and transport.** The canonical byte encoding is defined and
signed; how it is packaged, delivered to an air-gapped site and stored is a
deployment concern.

## What this does not claim

**Client-side licence enforcement is not tamper-proof and is not presented as
such.** A determined customer can patch the check out. That is true of every
client-side licence, and pretending otherwise leads to measures — dongles,
phone-home, kill switches — whose failure modes in a hospital are far worse
than the revenue they protect. ADR-034 records that as an accepted cost rather
than an oversight.

**The clock-rollback defence depends on storage this code cannot control.** A
deployment that keeps the high-water mark in a world-writable file has no
rollback defence at all.
