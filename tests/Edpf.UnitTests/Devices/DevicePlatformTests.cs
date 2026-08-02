using Edpf.Abstractions.Primitives;
using Edpf.Devices;
using Edpf.Healthcare.Domain;

namespace Edpf.UnitTests.Devices;

/// <summary>
/// Phase 24f — the device platform. *"Device data is never silently
/// trusted."*
/// </summary>
public sealed class DevicePlatformTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static DeviceRegistry RegistryWithCalibratedDevice(
        string deviceId = "ANALYZER-1", Guid? tenant = null)
    {
        var registry = new DeviceRegistry();
        registry.Register(new DeviceRegistration(
            deviceId,
            tenant ?? TenantA,
            "Acme-4000",
            DeviceTransport.Serial,
            calibratedUtc: Now.AddMonths(-1),
            calibrationExpiresUtc: Now.AddMonths(11)));
        return registry;
    }

    // ── calibration ────────────────────────────────────────────────────────

    [Fact]
    public void DeviceWithNoRecordedCalibration_IsNotTreatedAsCalibrated()
    {
        // "We never recorded it" and "it is fine" are different facts.
        // Defaulting the first to the second is how an uncalibrated instrument
        // stays in service for years.
        var device = new DeviceRegistration("SCALE-1", TenantA, "Acme-100", DeviceTransport.Serial);

        Assert.False(device.IsCalibrationValidAt(Now));
    }

    [Fact]
    public void LapsedCalibration_IsInvalid()
    {
        var device = new DeviceRegistration(
            "SCALE-1", TenantA, "Acme-100", DeviceTransport.Serial,
            calibratedUtc: Now.AddYears(-2), calibrationExpiresUtc: Now.AddYears(-1));

        Assert.False(device.IsCalibrationValidAt(Now));
        Assert.True(device.IsCalibrationValidAt(Now.AddMonths(-18)));
    }

    [Fact]
    public void ReadingDatedBeforeTheCalibration_IsNotCovered()
    {
        // Back-dated readings must not inherit a calibration performed later.
        var device = new DeviceRegistration(
            "SCALE-1", TenantA, "Acme-100", DeviceTransport.Serial,
            calibratedUtc: Now, calibrationExpiresUtc: Now.AddYears(1));

        Assert.False(device.IsCalibrationValidAt(Now.AddDays(-1)));
    }

    [Fact]
    public void CalibrationExpiringBeforeItWasPerformed_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new DeviceRegistration(
            "SCALE-1", TenantA, "Acme-100", DeviceTransport.Serial,
            calibratedUtc: Now, calibrationExpiresUtc: Now.AddDays(-1)));
    }

    [Fact]
    public void DevicesNeedingCalibration_ListsLapsedAndNeverCalibrated()
    {
        // The list a biomedical engineering department works from.
        var registry = new DeviceRegistry();
        registry.Register(new DeviceRegistration(
            "GOOD", TenantA, "M", DeviceTransport.Serial, Now.AddDays(-1), Now.AddYears(1)));
        registry.Register(new DeviceRegistration(
            "LAPSED", TenantA, "M", DeviceTransport.Serial, Now.AddYears(-2), Now.AddYears(-1)));
        registry.Register(new DeviceRegistration("NEVER", TenantA, "M", DeviceTransport.Serial));

        Assert.Equal(["LAPSED", "NEVER"], registry.DevicesNeedingCalibration(TenantA, Now));
    }

    [Fact]
    public void RecordingCalibration_RestoresValidity()
    {
        var registry = new DeviceRegistry();
        registry.Register(new DeviceRegistration("SCALE-1", TenantA, "M", DeviceTransport.Serial));

        registry.RecordCalibration(TenantA, "SCALE-1", Now, Now.AddYears(1));

        Assert.Empty(registry.DevicesNeedingCalibration(TenantA, Now));
    }

    // ── tenant scoping ─────────────────────────────────────────────────────

    [Fact]
    public void AnotherTenantsDevice_IsNotFound_NotForbidden()
    {
        // On a shared site, the set of instruments a neighbour runs is
        // commercial information; "forbidden" would confirm it exists.
        DeviceRegistry registry = RegistryWithCalibratedDevice(tenant: TenantA);

        Result<DeviceRegistration> found = registry.Find(TenantB, "ANALYZER-1");

        Assert.True(found.IsFailure);
        Assert.Equal(ErrorCategory.NotFound, found.Error!.Category);
    }

    [Fact]
    public void DuplicateRegistration_IsRefused()
    {
        // A silent replacement could substitute an uncalibrated instrument for
        // a calibrated one.
        DeviceRegistry registry = RegistryWithCalibratedDevice();

        Result second = registry.Register(new DeviceRegistration(
            "ANALYZER-1", TenantA, "Other", DeviceTransport.Usb));

        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.Duplicate, second.Error!.Code);
    }

    // ── reading acceptance ─────────────────────────────────────────────────

    private static ReadingValidator ValidatorWith(DeviceRegistry registry)
    {
        var validator = new ReadingValidator(registry);

        // Supplied by the vertical, not built into the core (ADR-024).
        foreach (PlausibilityRange range in ClinicalPlausibility.AdultVitalSigns())
        {
            validator.Declare(range);
        }

        return validator;
    }

    [Fact]
    public void ReadingFromAnUnregisteredDevice_IsRejected()
    {
        // Not a device with unknown calibration — a source nobody put there on
        // purpose.
        ReadingValidator validator = ValidatorWith(new DeviceRegistry());

        ReadingVerdict verdict = validator.Evaluate(
            TenantA, "GHOST", "HeartRate", "/min", 72m, Now);

        Assert.Equal(ReadingDisposition.Reject, verdict.Disposition);
    }

    [Fact]
    public void PerfectlyNormalReadingFromAnUncalibratedDevice_IsStillRejected()
    {
        // The order of the checks made concrete: entitlement before content.
        // A plausible number from an unverified instrument is the more
        // dangerous case, because nothing about it invites a second look.
        var registry = new DeviceRegistry();
        registry.Register(new DeviceRegistration("MONITOR-1", TenantA, "M", DeviceTransport.Serial));
        ReadingValidator validator = ValidatorWith(registry);

        ReadingVerdict verdict = validator.Evaluate(
            TenantA, "MONITOR-1", "HeartRate", "/min", 72m, Now);

        Assert.Equal(ReadingDisposition.Reject, verdict.Disposition);
        Assert.Contains("calibration", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImplausibleReading_IsRejectedAsAnArtefact()
    {
        // A rate above what cardiac tissue can sustain is a lead artefact, not
        // a measurement.
        ReadingValidator validator = ValidatorWith(RegistryWithCalibratedDevice("MONITOR-1"));

        ReadingVerdict verdict = validator.Evaluate(
            TenantA, "MONITOR-1", "HeartRate", "/min", 480m, Now);

        Assert.Equal(ReadingDisposition.Reject, verdict.Disposition);
        Assert.Contains("artefact", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AbnormalButPossibleReading_IsFlagged_NotDiscarded()
    {
        // **The distinction the two bands exist for.** A saturation of 82% is
        // a real emergency. Discarding it because it is abnormal would delete
        // the most important reading of the shift.
        ReadingValidator validator = ValidatorWith(RegistryWithCalibratedDevice("MONITOR-1"));

        ReadingVerdict verdict = validator.Evaluate(
            TenantA, "MONITOR-1", "OxygenSaturation", "%", 82m, Now);

        Assert.Equal(ReadingDisposition.Flag, verdict.Disposition);
        Assert.False(verdict.Accepted);
    }

    [Fact]
    public void NormalReading_IsAccepted()
    {
        ReadingValidator validator = ValidatorWith(RegistryWithCalibratedDevice("MONITOR-1"));

        ReadingVerdict verdict = validator.Evaluate(
            TenantA, "MONITOR-1", "OxygenSaturation", "%", 98m, Now);

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void UnitMismatch_IsRefused_NeverSilentlyConverted()
    {
        // A silent conversion is how a value in one unit becomes a number in
        // another. Phase 24's converter is the only thing allowed to change a
        // quantity's unit.
        ReadingValidator validator = ValidatorWith(RegistryWithCalibratedDevice("THERMO-1"));

        ReadingVerdict verdict = validator.Evaluate(
            TenantA, "THERMO-1", "BodyTemperature", "[degF]", 98.6m, Now);

        Assert.Equal(ReadingDisposition.Reject, verdict.Disposition);
        Assert.Contains("refused rather than converted", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void QuantityWithNoDeclaredRange_IsFlagged_NotAccepted()
    {
        // Nothing has said what a real measurement of this looks like.
        // Silence is not assurance.
        ReadingValidator validator = ValidatorWith(RegistryWithCalibratedDevice("MONITOR-1"));

        ReadingVerdict verdict = validator.Evaluate(
            TenantA, "MONITOR-1", "UndeclaredQuantity", "/min", 1m, Now);

        Assert.Equal(ReadingDisposition.Flag, verdict.Disposition);
    }

    [Fact]
    public void ExpectedBandOutsideThePlausibleBand_IsRefusedAtConstruction()
    {
        // A reading could otherwise be simultaneously normal and impossible.
        Assert.Throws<ArgumentException>(() => new PlausibilityRange(
            "X", "/min", plausibleMinimum: 10m, plausibleMaximum: 100m,
            expectedMinimum: 5m, expectedMaximum: 200m));
    }

    // ── the clinical bands live in the vertical ────────────────────────────

    [Fact]
    public void ClinicalBands_DistinguishArtefactFromEmergency()
    {
        // The reason the core carries no clinical constants (ADR-024): this
        // knowledge is domain knowledge, and it is what the two bands encode.
        PlausibilityRange heartRate = ClinicalPlausibility.AdultVitalSigns()[0];

        Assert.Equal("HeartRate", heartRate.Quantity);

        // 190 — real, and an emergency.
        Assert.True(heartRate.IsPlausible(190m));
        Assert.False(heartRate.IsExpected(190m));

        // 320 — beyond what cardiac tissue sustains; an artefact.
        Assert.False(heartRate.IsPlausible(320m));

        // 72 — unremarkable.
        Assert.True(heartRate.IsExpected(72m));
    }

    [Fact]
    public void ClinicalBands_AreDataInTheVertical_NotConstantsInTheCore()
    {
        // Asserted structurally rather than argued: the core's own type
        // carries no values until a vertical supplies them.
        var bare = new ReadingValidator(RegistryWithCalibratedDevice("MONITOR-1"));

        ReadingVerdict verdict = bare.Evaluate(
            TenantA, "MONITOR-1", "HeartRate", "/min", 72m, Now);

        // Without the vertical's declarations the core cannot judge a heart
        // rate at all — which is exactly right.
        Assert.Equal(ReadingDisposition.Flag, verdict.Disposition);
    }
}
