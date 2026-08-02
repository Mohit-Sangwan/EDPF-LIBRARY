using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Devices;

/// <summary>What the platform decided about one device reading (Phase 24f).</summary>
public enum ReadingDisposition
{
    /// <summary>The reading may be recorded.</summary>
    Accept = 0,

    /// <summary>
    /// The reading is held for a human to look at. It is neither recorded nor
    /// discarded.
    /// </summary>
    /// <remarks>
    /// The middle option is the important one. Discarding loses a reading that
    /// may have been the true one — a genuinely extreme result from a
    /// deteriorating subject looks exactly like an artefact — and accepting it
    /// puts an unverified number in a record someone will act on.
    /// </remarks>
    Flag = 1,

    /// <summary>
    /// The reading must not be recorded at all: the device was not entitled to
    /// produce it.
    /// </summary>
    Reject = 2,
}

/// <summary>
/// The bounds a measurement is expected to fall within (Phase 24f).
/// </summary>
/// <remarks>
/// <para>
/// **Deliberately domain-neutral.** A range is a quantity, two numbers and a
/// unit — the same shape whether it bounds a blood pressure, a batch weight or
/// a coolant temperature. The values that make it clinical are *data*, and
/// under ADR-024 they belong in the vertical that knows them, not in the core
/// that transports them.
/// </para>
/// <para>
/// Two bands rather than one. The **plausible** band is what a real
/// measurement of this quantity looks like; outside it the reading is almost
/// certainly an artefact — a disconnected lead, a probe in open air. The
/// **expected** band is the normal operating range; outside it the reading may
/// be perfectly real and highly significant. Collapsing the two into a single
/// range means either flagging every abnormal-but-true result or accepting
/// every impossible one.
/// </para>
/// </remarks>
public sealed class PlausibilityRange
{
    /// <summary>Initializes a range.</summary>
    /// <param name="quantity">What is measured.</param>
    /// <param name="unit">The unit, as a UCUM code.</param>
    /// <param name="plausibleMinimum">Below this the reading cannot be a real measurement.</param>
    /// <param name="plausibleMaximum">Above this the reading cannot be a real measurement.</param>
    /// <param name="expectedMinimum">Below this the reading is unusual but possible.</param>
    /// <param name="expectedMaximum">Above this the reading is unusual but possible.</param>
    /// <exception cref="ArgumentException">The bands are inverted or the expected band escapes the plausible one.</exception>
    public PlausibilityRange(
        string quantity,
        string unit,
        decimal plausibleMinimum,
        decimal plausibleMaximum,
        decimal expectedMinimum,
        decimal expectedMaximum)
    {
        Quantity = Guard.NotNullOrWhiteSpace(quantity, nameof(quantity));
        Unit = Guard.NotNullOrWhiteSpace(unit, nameof(unit));
        PlausibleMinimum = plausibleMinimum;
        PlausibleMaximum = plausibleMaximum;
        ExpectedMinimum = expectedMinimum;
        ExpectedMaximum = expectedMaximum;

        if (plausibleMaximum <= plausibleMinimum)
        {
            throw new ArgumentException(
                "The plausible maximum must exceed the plausible minimum.", nameof(plausibleMaximum));
        }

        if (expectedMaximum <= expectedMinimum)
        {
            throw new ArgumentException(
                "The expected maximum must exceed the expected minimum.", nameof(expectedMaximum));
        }

        // An expected band wider than the plausible one would mark readings
        // "normal" that the same range calls impossible.
        if (expectedMinimum < plausibleMinimum || expectedMaximum > plausibleMaximum)
        {
            throw new ArgumentException(
                "The expected band must sit inside the plausible band; otherwise a reading could be "
                + "simultaneously normal and impossible.",
                nameof(expectedMinimum));
        }
    }

    /// <summary>What is measured.</summary>
    public string Quantity { get; }

    /// <summary>The unit, as a UCUM code.</summary>
    public string Unit { get; }

    /// <summary>Below this the reading cannot be a real measurement.</summary>
    public decimal PlausibleMinimum { get; }

    /// <summary>Above this the reading cannot be a real measurement.</summary>
    public decimal PlausibleMaximum { get; }

    /// <summary>Below this the reading is unusual but possible.</summary>
    public decimal ExpectedMinimum { get; }

    /// <summary>Above this the reading is unusual but possible.</summary>
    public decimal ExpectedMaximum { get; }

    /// <summary>Whether the value could be a real measurement.</summary>
    /// <param name="value">The measured value.</param>
    /// <returns>Whether it falls inside the plausible band.</returns>
    public bool IsPlausible(decimal value)
        => value >= PlausibleMinimum && value <= PlausibleMaximum;

    /// <summary>Whether the value falls in the normal operating band.</summary>
    /// <param name="value">The measured value.</param>
    /// <returns>Whether it falls inside the expected band.</returns>
    public bool IsExpected(decimal value)
        => value >= ExpectedMinimum && value <= ExpectedMaximum;
}

/// <summary>A reading and what was decided about it (Phase 24f).</summary>
public sealed class ReadingVerdict
{
    /// <summary>Initializes a verdict.</summary>
    /// <param name="disposition">What was decided.</param>
    /// <param name="reason">Why, in terms a person reviewing a flagged reading can act on.</param>
    public ReadingVerdict(ReadingDisposition disposition, string reason)
    {
        Disposition = disposition;
        Reason = Guard.NotNullOrWhiteSpace(reason, nameof(reason));
    }

    /// <summary>What was decided.</summary>
    public ReadingDisposition Disposition { get; }

    /// <summary>Why.</summary>
    public string Reason { get; }

    /// <summary>Whether the reading may be recorded.</summary>
    public bool Accepted => Disposition == ReadingDisposition.Accept;
}

/// <summary>
/// Decides whether a device reading may be recorded (Phase 24f).
/// </summary>
/// <remarks>
/// <para>
/// *"Implausible-reading detection — device data is never silently trusted."*
/// </para>
/// <para>
/// A device is an unauthenticated input source sitting on a cable in a
/// corridor. It reports confidently whether or not it is calibrated, whether
/// or not its probe is attached, and whether or not it is the device anyone
/// thinks it is. Everything it says is a claim.
/// </para>
/// <para>
/// The checks run in a fixed order — **entitlement before content** — because
/// they answer different questions. An uncalibrated device's reading is
/// rejected whatever its value: a plausible number from an instrument nobody
/// has verified is more dangerous than an implausible one, because nothing
/// about it invites a second look.
/// </para>
/// </remarks>
public sealed class ReadingValidator
{
    private readonly DeviceRegistry _registry;
    private readonly Dictionary<string, PlausibilityRange> _ranges =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a validator.</summary>
    /// <param name="registry">The device registry.</param>
    public ReadingValidator(DeviceRegistry registry)
        => _registry = Guard.NotNull(registry, nameof(registry));

    /// <summary>
    /// Declares the bounds for a measured quantity.
    /// </summary>
    /// <param name="range">The range.</param>
    /// <returns>This validator, for chaining.</returns>
    /// <remarks>
    /// Supplied by the caller rather than built in. The core transports
    /// measurements; the vertical knows what a real one looks like (ADR-024).
    /// </remarks>
    public ReadingValidator Declare(PlausibilityRange range)
    {
        Guard.NotNull(range, nameof(range));
        _ranges[range.Quantity] = range;
        return this;
    }

    /// <summary>
    /// Decides whether a reading may be recorded.
    /// </summary>
    /// <param name="tenantId">The tenant the reading belongs to.</param>
    /// <param name="deviceId">The reporting device.</param>
    /// <param name="quantity">What was measured.</param>
    /// <param name="unit">The unit the device reported, as a UCUM code.</param>
    /// <param name="value">The measured value.</param>
    /// <param name="observedUtc">When the reading was taken.</param>
    /// <returns>The verdict.</returns>
    public ReadingVerdict Evaluate(
        Guid tenantId,
        string deviceId,
        string quantity,
        string unit,
        decimal value,
        DateTimeOffset observedUtc)
    {
        Result<DeviceRegistration> device = _registry.Find(tenantId, deviceId);

        if (device.IsFailure)
        {
            // An unregistered device is not a device with unknown calibration
            // — it is a source nobody put there on purpose.
            return new ReadingVerdict(
                ReadingDisposition.Reject,
                $"Device '{deviceId}' is not registered to this tenant.");
        }

        if (!device.Value.IsCalibrationValidAt(observedUtc))
        {
            // Rejected regardless of the value. A plausible number from an
            // unverified instrument is the more dangerous case, because
            // nothing about it invites a second look.
            return new ReadingVerdict(
                ReadingDisposition.Reject,
                $"Device '{deviceId}' had no valid calibration at the time of the reading.");
        }

        if (!_ranges.TryGetValue(quantity, out PlausibilityRange? range))
        {
            // No declared range means nothing has said what a real measurement
            // of this quantity looks like. Flagged rather than accepted:
            // silence is not assurance.
            return new ReadingVerdict(
                ReadingDisposition.Flag,
                $"No plausibility range is declared for '{quantity}', so the reading cannot be checked.");
        }

        if (!string.Equals(range.Unit, unit, StringComparison.Ordinal))
        {
            // Unit mismatch is refused, never converted here. A silent
            // conversion is how a value in one unit becomes a number in
            // another, and Phase 24's converter is the only thing allowed to
            // change a quantity's unit.
            return new ReadingVerdict(
                ReadingDisposition.Reject,
                $"Device reported '{quantity}' in '{unit}' but the declared range is in '{range.Unit}'. "
                + "The reading is refused rather than converted.");
        }

        if (!range.IsPlausible(value))
        {
            return new ReadingVerdict(
                ReadingDisposition.Reject,
                $"'{quantity}' of {value} {unit} falls outside the plausible band "
                + $"{range.PlausibleMinimum}-{range.PlausibleMaximum}; it is an artefact rather than a "
                + "measurement.");
        }

        if (!range.IsExpected(value))
        {
            // Real, and possibly the most important reading of the day.
            // Flagged for attention, not discarded.
            return new ReadingVerdict(
                ReadingDisposition.Flag,
                $"'{quantity}' of {value} {unit} is outside the expected band "
                + $"{range.ExpectedMinimum}-{range.ExpectedMaximum} but is a possible measurement.");
        }

        return new ReadingVerdict(ReadingDisposition.Accept, "Within the expected band.");
    }
}
