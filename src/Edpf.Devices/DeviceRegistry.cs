using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Devices;

/// <summary>How a device is attached (Phase 24f).</summary>
public enum DeviceTransport
{
    /// <summary>RS-232 serial. Still ubiquitous in laboratory instruments.</summary>
    Serial = 0,

    /// <summary>USB.</summary>
    Usb = 1,

    /// <summary>Bluetooth or BLE.</summary>
    Bluetooth = 2,

    /// <summary>HID — scanners, signature pads, card readers.</summary>
    HumanInterface = 3,

    /// <summary>Network-attached.</summary>
    Network = 4,
}

/// <summary>
/// A device known to the platform, and whether it may be trusted right now
/// (Phase 24f).
/// </summary>
/// <remarks>
/// <para>
/// **Calibration is not paperwork.** An instrument past its calibration date
/// still returns numbers, confidently and in the right format. Nothing about
/// the reading says it is wrong. The registry is what stands between that
/// reading and a record someone will act on.
/// </para>
/// <para>
/// So calibration expiry is held here rather than in a spreadsheet, and
/// <see cref="IsCalibrationValidAt"/> is consulted on every reading rather
/// than reviewed monthly.
/// </para>
/// </remarks>
public sealed class DeviceRegistration
{
    /// <summary>Initializes a registration.</summary>
    /// <param name="deviceId">The device identifier.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="model">The manufacturer's model, which selects the protocol parser.</param>
    /// <param name="transport">How it is attached.</param>
    /// <param name="calibratedUtc">When it was last calibrated, if ever.</param>
    /// <param name="calibrationExpiresUtc">When that calibration lapses, if it does.</param>
    /// <exception cref="ArgumentException">The calibration window is inverted.</exception>
    public DeviceRegistration(
        string deviceId,
        Guid tenantId,
        string model,
        DeviceTransport transport,
        DateTimeOffset? calibratedUtc = null,
        DateTimeOffset? calibrationExpiresUtc = null)
    {
        DeviceId = Guard.NotNullOrWhiteSpace(deviceId, nameof(deviceId));
        TenantId = Guard.NotDefault(tenantId, nameof(tenantId));
        Model = Guard.NotNullOrWhiteSpace(model, nameof(model));
        Transport = transport;
        CalibratedUtc = calibratedUtc;
        CalibrationExpiresUtc = calibrationExpiresUtc;

        if (calibratedUtc.HasValue
            && calibrationExpiresUtc.HasValue
            && calibrationExpiresUtc.Value <= calibratedUtc.Value)
        {
            throw new ArgumentException(
                "A calibration cannot expire before or when it was performed.",
                nameof(calibrationExpiresUtc));
        }
    }

    /// <summary>The device identifier.</summary>
    public string DeviceId { get; }

    /// <summary>The owning tenant.</summary>
    public Guid TenantId { get; }

    /// <summary>The manufacturer's model, which selects the protocol parser.</summary>
    public string Model { get; }

    /// <summary>How it is attached.</summary>
    public DeviceTransport Transport { get; }

    /// <summary>When it was last calibrated, if ever.</summary>
    public DateTimeOffset? CalibratedUtc { get; }

    /// <summary>When that calibration lapses, if it does.</summary>
    public DateTimeOffset? CalibrationExpiresUtc { get; }

    /// <summary>
    /// Whether the device's calibration is valid at <paramref name="asOf"/>.
    /// </summary>
    /// <param name="asOf">The instant to test.</param>
    /// <returns>Whether readings taken then may be trusted.</returns>
    /// <remarks>
    /// **An unrecorded calibration is not a valid one.** A device with no
    /// calibration date returns <see langword="false"/>, not
    /// <see langword="true"/>: "we never recorded it" and "it is fine" are
    /// different facts, and defaulting the first to the second is how an
    /// uncalibrated instrument stays in service for years.
    /// </remarks>
    public bool IsCalibrationValidAt(DateTimeOffset asOf)
    {
        if (!CalibratedUtc.HasValue)
        {
            return false;
        }

        if (asOf < CalibratedUtc.Value)
        {
            return false;
        }

        return !CalibrationExpiresUtc.HasValue || asOf < CalibrationExpiresUtc.Value;
    }
}

/// <summary>
/// The devices the platform knows about, scoped by tenant (Phase 24f).
/// </summary>
/// <remarks>
/// Tenant-scoped for the same reason everything else is: a shared analyzer in
/// a building housing two organisations must not let one read the other's
/// results, and the device id is the join key that would let it.
/// </remarks>
public sealed class DeviceRegistry
{
    private readonly Dictionary<string, DeviceRegistration> _devices =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a device.
    /// </summary>
    /// <param name="registration">The device.</param>
    /// <returns>Success, or a failure when the id is already registered.</returns>
    public Result Register(DeviceRegistration registration)
    {
        Guard.NotNull(registration, nameof(registration));

        string key = Key(registration.TenantId, registration.DeviceId);

        if (_devices.ContainsKey(key))
        {
            return Result.Failure(new Error(
                ErrorCodes.Duplicate,
                $"A device with id '{registration.DeviceId}' is already registered. A silent replacement "
                + "could substitute an uncalibrated instrument for a calibrated one.",
                ErrorCategory.Conflict));
        }

        _devices[key] = registration;
        return Result.Success();
    }

    /// <summary>
    /// Records a new calibration.
    /// </summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="calibratedUtc">When it was calibrated.</param>
    /// <param name="expiresUtc">When that calibration lapses.</param>
    /// <returns>Success, or a failure when the device is unknown.</returns>
    public Result RecordCalibration(
        Guid tenantId, string deviceId, DateTimeOffset calibratedUtc, DateTimeOffset? expiresUtc)
    {
        Result<DeviceRegistration> found = Find(tenantId, deviceId);
        if (found.IsFailure)
        {
            return Result.Failure(found.Error!);
        }

        DeviceRegistration existing = found.Value;

        _devices[Key(tenantId, deviceId)] = new DeviceRegistration(
            existing.DeviceId,
            existing.TenantId,
            existing.Model,
            existing.Transport,
            calibratedUtc,
            expiresUtc);

        return Result.Success();
    }

    /// <summary>
    /// Finds a device within a tenant.
    /// </summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="deviceId">The device.</param>
    /// <returns>The registration, or a failure.</returns>
    /// <remarks>
    /// A device belonging to another tenant is reported as not found, never as
    /// forbidden — "forbidden" would confirm the device exists, and on a shared
    /// site the set of instruments a neighbour runs is itself commercial
    /// information.
    /// </remarks>
    public Result<DeviceRegistration> Find(Guid tenantId, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return NotFound(deviceId);
        }

        return _devices.TryGetValue(Key(tenantId, deviceId), out DeviceRegistration? device)
            ? Result.Success(device)
            : NotFound(deviceId);
    }

    /// <summary>
    /// Devices whose calibration has lapsed or was never recorded.
    /// </summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="asOf">The instant to assess.</param>
    /// <returns>The device ids, sorted.</returns>
    /// <remarks>
    /// The list a biomedical engineering department works from. Sorted so two
    /// runs produce a diffable report.
    /// </remarks>
    public IReadOnlyList<string> DevicesNeedingCalibration(Guid tenantId, DateTimeOffset asOf)
    {
        var lapsed = new List<string>();

        foreach (KeyValuePair<string, DeviceRegistration> entry in _devices)
        {
            if (entry.Value.TenantId == tenantId && !entry.Value.IsCalibrationValidAt(asOf))
            {
                lapsed.Add(entry.Value.DeviceId);
            }
        }

        lapsed.Sort(StringComparer.Ordinal);
        return lapsed;
    }

    private static Result<DeviceRegistration> NotFound(string? deviceId)
        => Result.Failure<DeviceRegistration>(new Error(
            ErrorCodes.NotFound,
            $"No device '{deviceId}' is registered.",
            ErrorCategory.NotFound));

    private static string Key(Guid tenantId, string deviceId)
        => tenantId.ToString("N") + "|" + deviceId;
}
