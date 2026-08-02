# ADR-029 — A device reading is a claim; entitlement is checked before content

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 24f — Device & Peripheral Platform
- **Related:** [ADR-002](ADR-002-multi-target-strategy.md) (this is the Tier 3 justification), [ADR-024](ADR-024-vertical-package-boundary.md) (clinical bands live in the vertical), [ADR-023](ADR-023-integrate-do-not-build.md) (UCUM units)

## Context

Locally-attached peripherals are how most hospital equipment still connects.
*"RS-232/serial remains ubiquitous in laboratory analyzers, and a framework
targeting LIS that cannot read a serial port is not deployable."*

A device is an **unauthenticated input source sitting on a cable in a
corridor**. It reports confidently whether or not it is calibrated, whether or
not its probe is attached, and whether or not it is the device anyone thinks it
is. Nothing about a reading distinguishes a good one from a bad one — that is
the whole problem, and it is why the phase says *"device data is never silently
trusted."*

Two distinctions turn out to carry the design.

**Entitlement is not content.** An instrument past its calibration date returns
numbers in the right format and the right range. A reading of 72 from an
uncalibrated monitor looks exactly like a reading of 72 from a calibrated one.

**Impossible is not abnormal.** A heart rate of 320 cannot be a measurement —
cardiac tissue does not sustain it, so the lead is off. A heart rate of 190 is
entirely real and may be the most important thing anyone sees that shift.
Treating both as "out of range" forces a choice between discarding true
emergencies and accepting impossible numbers.

## Decision

**Readings are validated entitlement-first, and the plausible/expected
distinction is modelled explicitly.**

1. **Order: registered → calibrated → declared → unit → plausible → expected.**
   An uncalibrated device's reading is rejected *whatever its value*. A
   plausible number from an unverified instrument is the more dangerous case,
   precisely because nothing about it invites a second look.

2. **An unrecorded calibration is not a valid one.** A device with no
   calibration date is invalid, not valid-by-default. "We never recorded it"
   and "it is fine" are different facts, and defaulting the first to the
   second is how an uncalibrated instrument stays in service for years.

3. **Three dispositions, not two.** `Accept`, `Flag`, `Reject`. The middle one
   is load-bearing: discarding an abnormal reading loses one that may have
   been true, and accepting it puts an unverified number in a record someone
   will act on. Flagging holds it for a human.

4. **A quantity with no declared range is flagged, never accepted.** Nothing
   has said what a real measurement looks like, and silence is not assurance.

5. **A unit mismatch is refused, never converted.** Phase 24's UCUM converter
   is the only thing permitted to change a quantity's unit; a silent
   conversion here is how a value in one unit becomes a number in another.

6. **The bands are data supplied by the vertical (ADR-024).** A device
   registry, a calibration lifecycle and a framing protocol serve laboratory
   analyzers, industrial scales and payment terminals alike, so they are core.
   The knowledge that 320 is an artefact and 190 is an emergency is clinical,
   so it is in `verticals/Edpf.Healthcare.Domain`. The core cannot judge a
   heart rate at all until a vertical tells it how — and there is a test
   asserting exactly that.

7. **This assembly targets Tier 3 (net472/net48).** The hardware lives in
   desktop and Windows Service hosts, and those hosts are the ones still on
   .NET Framework in the hospitals that own the analyzers. ADR-002 named this
   as the justification for keeping Tier 3; this is that justification being
   spent rather than merely claimed.

## Consequences

### Accepted costs

- **Tier 3 constrains this assembly permanently.** No `DateOnly`, no record
  structs, no `Index`/`Range`, no `System.Buffers`. `AstmFrame` uses
  `Substring` where a `Range` would read better. That cost is the price of the
  capability, and paying it here is the reason Tier 3 stays in the matrix.
- **Every deployment must supply its own bands.** The shipped adult vital-sign
  set is explicitly adult: a neonatal heart rate of 160 is unremarkable and
  would be flagged. Shipping one set and calling it universal would be the
  more dangerous choice, so bands are exposed as data rather than applied
  automatically.
- **Flagged readings need somewhere to go.** A disposition nobody reviews is
  a rejection with extra steps. The queue and its staffing are the adopter's
  responsibility, and belong in the shared-responsibility model.
- **Calibration state is only as good as what is recorded.** The registry
  gates on recorded expiry; it cannot know that an instrument drifted the day
  after it was certified.

### What this does not claim

- **No serial, USB, Bluetooth or HID I/O is implemented.** `DeviceTransport`
  names them; nothing here opens a port. Driving real hardware cannot be
  verified without hardware, and per Z.12 a claimed capability is a tested
  capability. What is delivered is everything that decides *whether to believe*
  a reading, which is the part that is safety-critical and the part that is
  testable.
- **ASTM E1381 framing is implemented; E1394 record content is not.** Framing
  is the layer where a corrupted result becomes a wrong result, and its
  checksum is verifiable by hand. Parsing `R|1|^^^Glucose|5.4|mmol/L` into a
  clinical observation needs the terminology binding ADR-023 governs.
- **The plausibility bands are conventional, not validated.** They are drawn
  from ordinary clinical reference ranges, and a deployment should have a
  clinician confirm them. They are shipped as a starting point that fails
  safe, not as a validated data set.

## Revisit triggers

- **Anyone proposes accepting a reading from an uncalibrated device**, for any
  reason including "the calibration paperwork is late". That is the decision
  this ADR exists to make expensive.
- **The Flag disposition is routinely auto-accepted downstream.** Then the
  three-way distinction has collapsed to two in practice and the model is not
  earning its complexity.
- **A vertical needs bands that vary by subject** (paediatric, neonatal,
  pregnancy). The current model is one band per quantity; selecting by
  population is a real requirement and a real design change.
- **Tier 3 support is proposed for removal.** This assembly is the stated
  justification; if the devices move to hosts that run modern .NET, ADR-002's
  cost/benefit changes and both decisions should be reopened together.
