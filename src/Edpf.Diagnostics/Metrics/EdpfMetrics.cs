using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Edpf.Diagnostics.Metrics;

/// <summary>
/// The RED (rate, errors, duration) and resource instruments of Phase 05 §④.
/// One meter for the whole framework so operators subscribe to a single
/// stable name; Phase 30 turns these signals into SLOs and alerts.
/// </summary>
/// <remarks>
/// Instrument tags carry tenant id, operation name and outcome — never a
/// subject identifier, a key, or any classified value. A metric dimension is
/// a log line with worse retention: high-cardinality PHI in a tag is both a
/// breach and a cost incident.
/// </remarks>
public sealed class EdpfMetrics : IDisposable
{
    private readonly Meter _meter;
    private bool _disposed;

    /// <summary>Creates the framework meter and its instruments.</summary>
    public EdpfMetrics()
    {
        _meter = new Meter(EdpfDiagnosticNames.MeterName);

        OperationsTotal = _meter.CreateCounter<long>(
            "edpf.operations",
            unit: "{operation}",
            description: "Operations executed (RED: rate).");

        OperationErrorsTotal = _meter.CreateCounter<long>(
            "edpf.operation.errors",
            unit: "{error}",
            description: "Operations that failed, tagged by stable error code (RED: errors).");

        OperationDuration = _meter.CreateHistogram<double>(
            "edpf.operation.duration",
            unit: "ms",
            description: "Operation duration (RED: duration).");

        ConnectionPoolInUse = _meter.CreateUpDownCounter<long>(
            "edpf.connections.in_use",
            unit: "{connection}",
            description: "Connections currently checked out (USE: utilisation).");

        AuditWriteDuration = _meter.CreateHistogram<double>(
            "edpf.audit.write.duration",
            unit: "ms",
            description: "Audit append duration; budgeted at under 3% of p99 (NFR sheet).");

        OutboxPendingDepth = _meter.CreateUpDownCounter<long>(
            "edpf.outbox.pending",
            unit: "{message}",
            description: "Undispatched outbox messages (USE: saturation).");
    }

    /// <summary>The framework <see cref="System.Diagnostics.ActivitySource"/> for tracing.</summary>
    public static ActivitySource ActivitySource { get; } = new(EdpfDiagnosticNames.ActivitySourceName);

    /// <summary>Operations executed.</summary>
    public Counter<long> OperationsTotal { get; }

    /// <summary>Operations that failed, tagged by stable error code.</summary>
    public Counter<long> OperationErrorsTotal { get; }

    /// <summary>Operation duration in milliseconds.</summary>
    public Histogram<double> OperationDuration { get; }

    /// <summary>Connections currently checked out.</summary>
    public UpDownCounter<long> ConnectionPoolInUse { get; }

    /// <summary>Audit append duration in milliseconds.</summary>
    public Histogram<double> AuditWriteDuration { get; }

    /// <summary>Undispatched outbox messages.</summary>
    public UpDownCounter<long> OutboxPendingDepth { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _meter.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Standard metric tag names, fixed so dashboards survive refactors.
/// </summary>
/// <remarks>
/// Tags carry operation, tenant and outcome — never a subject identifier or
/// any classified value. A metric dimension is a log line with worse
/// retention: high-cardinality PHI in a tag is both a breach and a cost
/// incident.
/// </remarks>
public static class EdpfMetricTags
{
    /// <summary>The operation name, e.g. <c>patient.create</c>.</summary>
    public const string Operation = "edpf.operation";

    /// <summary>The tenant id. Internal classification — an opaque GUID, never a name.</summary>
    public const string TenantId = "edpf.tenant_id";

    /// <summary><c>success</c> or <c>failure</c>.</summary>
    public const string Outcome = "edpf.outcome";

    /// <summary>The stable EDPF error code on failure.</summary>
    public const string ErrorCode = "edpf.error_code";

    /// <summary>The data provider serving the operation.</summary>
    public const string Provider = "edpf.provider";
}
