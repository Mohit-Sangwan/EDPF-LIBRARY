# Phase 24f — Device & Peripheral Platform

**Status:** Partial — trust and framing complete; transport I/O deferred with reasons
**Gate contribution:** G5 (Domain Capability)
**ADR produced:** [ADR-029 — A device reading is a claim](../adr/ADR-029-device-readings-are-claims.md)

## What this phase is

Phase 24c covered networked IoMT. This covers **locally-attached
peripherals**, which is how most hospital equipment still connects.

| File | Contents |
| --- | --- |
| [`DeviceRegistry.cs`](../../src/Edpf.Devices/DeviceRegistry.cs) | Device identity, calibration lifecycle, tenant scoping |
| [`ReadingAcceptance.cs`](../../src/Edpf.Devices/ReadingAcceptance.cs) | Plausibility bands and the entitlement-first validator |
| [`AstmFrame.cs`](../../src/Edpf.Devices/AstmFrame.cs) | ASTM E1381 framing with checksum and sequence |
| [`ClinicalPlausibility.cs`](../../verticals/Edpf.Healthcare.Domain/ClinicalPlausibility.cs) | Adult vital-sign bands — **in the vertical**, per ADR-024 |

## The two distinctions this phase turns on

### Entitlement is not content

An instrument past its calibration date returns numbers in the right format
and the right range. A reading of 72 from an uncalibrated monitor looks
exactly like a reading of 72 from a calibrated one.

So the checks run **entitlement first**: registered → calibrated → declared →
unit → plausible → expected. An uncalibrated device's reading is rejected
whatever its value, because a plausible number from an unverified instrument is
the *more* dangerous case — nothing about it invites a second look.

`PerfectlyNormalReadingFromAnUncalibratedDevice_IsStillRejected` is the test
that pins the ordering.

And an unrecorded calibration is **not** a valid one. "We never recorded it"
and "it is fine" are different facts; defaulting the first to the second is how
an uncalibrated instrument stays in service for years.

### Impossible is not abnormal

A heart rate of 320 cannot be a measurement — cardiac tissue does not sustain
it, so the lead is off. A heart rate of 190 is entirely real and may be the
most important thing anyone sees that shift.

Hence two bands and **three dispositions**. The middle one is load-bearing:

| | |
| --- | --- |
| Outside the **plausible** band | `Reject` — an artefact, not a measurement |
| Outside the **expected** band, inside plausible | `Flag` — real, possibly urgent, held for a human |
| Inside both | `Accept` |

A saturation of 82% is a real emergency. Discarding it because it is abnormal
would delete the most important reading of the shift; accepting it silently
puts an unreviewed number in a record. Flagging is the only honest answer.

## ADR-024, working a second time

The core carries **no clinical constants**. A device registry, a calibration
lifecycle and a framing protocol serve laboratory analyzers, industrial scales
and payment terminals alike. The knowledge that 320 is an artefact and 190 is
an emergency is clinical, and lives in
`verticals/Edpf.Healthcare.Domain`.

`ClinicalBands_AreDataInTheVertical_NotConstantsInTheCore` asserts it
structurally: without the vertical's declarations the core cannot judge a heart
rate at all — it flags it as uncheckable. That is exactly right.

This phase also closed a real gap in the enforcement itself. `CoreNeutralityTests`
listed eleven core assemblies, and **six projects added since it was written
were outside its scope** — `Edpf.Metadata`, `Edpf.Formula`, `Edpf.Rules`,
`Edpf.Barcode`, `Edpf.DataQuality` and `Edpf.Devices`. All six turned out to be
neutral already, but they were neutral unguarded. They are now in the list.

## Tier 3, spent rather than claimed

This assembly targets **net472 and net48**. ADR-002 named locally-attached
peripherals as one of the few genuine justifications for keeping Tier 3, and
this is that justification being spent: the hardware lives in desktop and
Windows Service hosts, and those hosts are the ones still on .NET Framework in
the hospitals that own the analyzers.

The constraint is real and visible in the code — `AstmFrame` uses `Substring`
where a `Range` expression would read better, with a comment naming ADR-002.
It built clean across all five target frameworks on the first attempt, which is
what six prior encounters with the rule bought.

## ASTM E1381 framing

A serial cable in a laboratory runs past centrifuges and refrigeration
compressors. Line noise flips bits, and **a flipped bit in a result value is a
wrong result delivered with full confidence**.

- The checksum makes that corruption visible. `SingleFlippedBit_IsDetected`
  corrupts one character of a payload and asserts the frame is refused.
- The cycling frame number makes a dropped or duplicated frame visible — a
  retransmission that silently replaces a *different* result is worse than a
  gap, because a gap gets noticed.
- A framing control character inside a payload is **refused, not escaped**:
  E1381 has no escape sequence, and the remainder would be read as a different
  frame. Same shape as the GS1 separator rule in Phase 17c.

The checksum test uses a hand-verifiable example — body `"1A"` + ETX is
`0x31 + 0x41 + 0x03 = 0x75` — so a reader can check it without running it.

## Exit criteria

| Criterion | Status |
| --- | --- |
| Device registry with calibration status and expiry | Met |
| Implausible-reading detection | Met — with the artefact/emergency distinction |
| Protocol framing for laboratory instruments | Met — E1381 |
| Tier 3 capability | Met — builds on net472/net48 |
| Serial / USB / Bluetooth / HID transport I/O | **Deferred** |
| Connection supervision with auto-reconnect | **Deferred** |

## Deferred, with reasons

**Transport I/O.** `DeviceTransport` names serial, USB, Bluetooth and HID;
nothing here opens a port. Driving real hardware cannot be verified without
hardware, and per Z.12 a claimed capability is a tested capability. A
`SerialPortDevice` that has never talked to an analyzer would be a claim.

What *is* delivered is everything that decides **whether to believe a
reading** — which is the safety-critical part, and the part that is testable
without a laboratory.

**ASTM E1394 record content.** Framing is implemented; parsing
`R|1|^^^Glucose|5.4|mmol/L` into a clinical observation is not. Framing is the
layer where a corrupted result becomes a wrong result, and its checksum is
verifiable by hand. Record content needs the terminology binding ADR-023
governs — LOINC codes, UCUM units, and a mapping table that is reference data
rather than code.

**Band validation.** The shipped adult vital-sign bands are drawn from ordinary
clinical reference ranges and are a starting point that fails safe, not a
validated data set. They are explicitly adult — a neonatal heart rate of 160 is
unremarkable and would be flagged here. A deployment should have a clinician
confirm them, and ADR-029 records that rather than leaving it implied.
