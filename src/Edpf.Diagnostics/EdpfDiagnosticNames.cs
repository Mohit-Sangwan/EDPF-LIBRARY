namespace Edpf.Diagnostics;

/// <summary>
/// Canonical instrumentation names. One activity source and one meter for the
/// whole framework, so operators subscribe to a single, stable name (Phase 05
/// generalizes; declared here per Phase 00 §⑥).
/// </summary>
public static class EdpfDiagnosticNames
{
    /// <summary>The framework-wide <c>ActivitySource</c> name.</summary>
    public const string ActivitySourceName = "Edpf";

    /// <summary>The framework-wide <c>Meter</c> name.</summary>
    public const string MeterName = "Edpf";

    /// <summary>Header carrying the correlation id across service boundaries.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>Header carrying the tenant resolution key (walking-skeleton strategy, ADR-004).</summary>
    public const string TenantHeader = "X-Tenant-Id";

    /// <summary>Header carrying the caller's idempotency key (ADR-003).</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";
}
