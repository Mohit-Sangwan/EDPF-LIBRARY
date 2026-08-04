using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;
using Edpf.Core.Time;

namespace Edpf.Documents;

/// <summary>A physical or virtual destination for a print job.</summary>
/// <remarks>
/// Declared per device rather than per job, because the constraint is physical:
/// a ward printer standing in a corridor is a different disclosure risk from
/// one in a locked records office, and the person who knows which is which is
/// the person who registered the device.
/// </remarks>
public sealed class PrintDestination
{
    /// <summary>
    /// Registers a destination.
    /// </summary>
    /// <param name="deviceId">The device's stable id.</param>
    /// <param name="location">Where it physically is. Appears in the audit trail.</param>
    /// <param name="maximumClassification">The highest classification it may print.</param>
    /// <param name="acceptedContentTypes">What the device can render.</param>
    /// <exception cref="ArgumentNullException"><paramref name="acceptedContentTypes"/> is null.</exception>
    /// <exception cref="ArgumentException">The id or location is blank.</exception>
    public PrintDestination(
        string deviceId,
        string location,
        DataClassificationLevel maximumClassification,
        IReadOnlyList<string> acceptedContentTypes)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A print destination requires a device id.", nameof(deviceId));
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException(
                "A print destination requires a location. Where the paper comes out is the control, and an "
                + "unlocated printer cannot be risk-assessed.",
                nameof(location));
        }

        DeviceId = deviceId;
        Location = location;
        MaximumClassification = maximumClassification;
        AcceptedContentTypes = acceptedContentTypes ?? throw new ArgumentNullException(nameof(acceptedContentTypes));
    }

    /// <summary>The device's stable id.</summary>
    public string DeviceId { get; }

    /// <summary>Where it physically is.</summary>
    public string Location { get; }

    /// <summary>The highest classification it may print.</summary>
    public DataClassificationLevel MaximumClassification { get; }

    /// <summary>What the device can render.</summary>
    public IReadOnlyList<string> AcceptedContentTypes { get; }
}

/// <summary>The driver seam — IPP, a spooler, a label-printer protocol, a test double.</summary>
public interface IPrintTransport
{
    /// <summary>A stable name for diagnostics.</summary>
    string TransportName { get; }

    /// <summary>
    /// Sends bytes to a device.
    /// </summary>
    /// <param name="destination">The registered destination.</param>
    /// <param name="document">The artefact to print.</param>
    /// <param name="cancellationToken">Cancels the job.</param>
    /// <returns>Success, or a failure carrying no document content.</returns>
    Task<Result> SubmitAsync(
        PrintDestination destination, RenderedDocument document, CancellationToken cancellationToken);
}

/// <summary>One print that happened, for the audit trail.</summary>
public sealed class PrintRecord
{
    /// <summary>Records a print.</summary>
    /// <param name="deviceId">Which device.</param>
    /// <param name="location">Where it physically is.</param>
    /// <param name="documentHash">Which exact artefact.</param>
    /// <param name="classification">What it carried.</param>
    /// <param name="requestedBy">Who asked.</param>
    /// <param name="occurredUtc">When.</param>
    /// <param name="tenantId">The tenant.</param>
    public PrintRecord(
        string deviceId,
        string location,
        string documentHash,
        DataClassificationLevel classification,
        string requestedBy,
        DateTimeOffset occurredUtc,
        Guid tenantId)
    {
        DeviceId = deviceId;
        Location = location;
        DocumentHash = documentHash;
        Classification = classification;
        RequestedBy = requestedBy;
        OccurredUtc = occurredUtc;
        TenantId = tenantId;
    }

    /// <summary>Which device.</summary>
    public string DeviceId { get; }

    /// <summary>Where it physically is.</summary>
    public string Location { get; }

    /// <summary>Which exact artefact — the hash, never the content.</summary>
    public string DocumentHash { get; }

    /// <summary>What it carried.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>Who asked.</summary>
    public string RequestedBy { get; }

    /// <summary>When.</summary>
    public DateTimeOffset OccurredUtc { get; }

    /// <summary>The tenant.</summary>
    public Guid TenantId { get; }
}

/// <summary>
/// Drives printers, and refuses to print classified material to a device that
/// was not registered for it.
/// </summary>
/// <remarks>
/// <para>
/// The review that prompted this observed that EDPF *audits* printing but
/// cannot *drive* a printer. Both halves matter, and the second is the one with
/// a control attached: a printed document has left every technical boundary the
/// platform has. Encryption at rest, tenant predicates and field authorization
/// all stop at the paper tray.
/// </para>
/// <para>
/// So the last enforceable decision is which tray, and it is made against a
/// ceiling the device declared — not against a flag on the job, which is the
/// caller asserting something about a room they may never have seen.
/// </para>
/// </remarks>
public sealed class PrintService
{
    private readonly Dictionary<string, PrintDestination> _destinations;
    private readonly IPrintTransport _transport;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IClock _clock;
    private readonly List<PrintRecord> _records = [];

    /// <summary>
    /// Composes the service over a registered device set.
    /// </summary>
    /// <param name="destinations">Every device this deployment may print to.</param>
    /// <param name="transport">The driver.</param>
    /// <param name="tenantAccessor">Ambient tenant.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    public PrintService(
        IReadOnlyList<PrintDestination> destinations,
        IPrintTransport transport,
        ITenantContextAccessor tenantAccessor,
        IClock clock)
    {
        Guard.NotNull(destinations, nameof(destinations));
        _transport = Guard.NotNull(transport, nameof(transport));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));
        _clock = Guard.NotNull(clock, nameof(clock));

        _destinations = new Dictionary<string, PrintDestination>(StringComparer.Ordinal);
        foreach (PrintDestination destination in destinations)
        {
            _destinations[destination.DeviceId] = destination;
        }
    }

    /// <summary>Every print that happened, oldest first.</summary>
    public IReadOnlyList<PrintRecord> Records => _records;

    /// <summary>
    /// Prints an artefact to a registered device.
    /// </summary>
    /// <param name="deviceId">The destination.</param>
    /// <param name="document">The exact artefact.</param>
    /// <param name="requestedBy">Who asked.</param>
    /// <param name="cancellationToken">Cancels the job.</param>
    /// <returns>Success, or a failure explaining the refusal.</returns>
    public async Task<Result> PrintAsync(
        string deviceId,
        RenderedDocument document,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(deviceId, nameof(deviceId));
        Guard.NotNull(document, nameof(document));
        Guard.NotNullOrWhiteSpace(requestedBy, nameof(requestedBy));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Result.Failure(NotFound());
        }

        if (!_destinations.TryGetValue(deviceId, out PrintDestination? destination))
        {
            return Result.Failure(NotFound());
        }

        if (document.Classification > destination.MaximumClassification)
        {
            return Result.Failure(new Error(
                ErrorCodes.ChannelClassificationExceeded,
                "Device " + destination.DeviceId + " at " + destination.Location + " prints at most "
                + destination.MaximumClassification + " content.",
                ErrorCategory.Compliance));
        }

        if (!Accepts(destination.AcceptedContentTypes, document.ContentType))
        {
            return Result.Failure(new Error(
                ErrorCodes.CapabilityNotSupported,
                "Device " + destination.DeviceId + " does not accept " + document.ContentType + ".",
                ErrorCategory.Validation));
        }

        Result submitted = await _transport
            .SubmitAsync(destination, document, cancellationToken)
            .ConfigureAwait(false);

        // Recorded on submission, success or not. A job the spooler rejected
        // may still have reached the device, and an audit trail that only lists
        // successes cannot answer "did this ever come out of that printer".
        _records.Add(new PrintRecord(
            destination.DeviceId,
            destination.Location,
            document.ContentHash,
            document.Classification,
            requestedBy,
            StorableInstant.Normalize(_clock.UtcNow),
            tenant.TenantId));

        return submitted;
    }

    private static bool Accepts(IReadOnlyList<string> accepted, string contentType)
    {
        for (int i = 0; i < accepted.Count; i++)
        {
            if (string.Equals(accepted[i], contentType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Error NotFound() => new(
        ErrorCodes.NotFound,
        "The requested resource was not found.",
        ErrorCategory.NotFound);
}

/// <summary>Records submissions in memory. Tests and development.</summary>
public sealed class RecordingPrintTransport : IPrintTransport
{
    /// <inheritdoc />
    public string TransportName => "Recording";

    /// <summary>Every submission attempted.</summary>
    public System.Collections.ObjectModel.Collection<(string DeviceId, string DocumentHash)> Submissions
    { get; } = [];

    /// <inheritdoc />
    public Task<Result> SubmitAsync(
        PrintDestination destination, RenderedDocument document, CancellationToken cancellationToken)
    {
        Guard.NotNull(destination, nameof(destination));
        Guard.NotNull(document, nameof(document));

        Submissions.Add((destination.DeviceId, document.ContentHash));
        return Task.FromResult(Result.Success());
    }
}
