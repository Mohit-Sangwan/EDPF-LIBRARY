namespace Edpf.WalkingSkeleton.Api.Pipeline;

/// <summary>
/// The fixed cross-cutting stage order of ADR-012. Every subsequent phase
/// plugs into this order; changing it requires superseding the ADR. The
/// architecture test <c>Adr012PipelineOrderTests</c> asserts that
/// <c>Program</c> composes middleware in exactly this sequence — audit can
/// never run before authorization, tenant resolution never after data access.
/// </summary>
public static class PipelineStages
{
    /// <summary>The canonical stage order (ADR-012).</summary>
    public static readonly IReadOnlyList<string> CanonicalOrder =
    [
        Correlation,
        TenantResolution,
        Authentication,
        Authorization,
        Validation,
        Idempotency,
        Handler,
        Transaction,
        Audit,
        Telemetry,
        Response,
    ];

    public const string Correlation = "Correlation";
    public const string TenantResolution = "TenantResolution";
    public const string Authentication = "Authentication";
    public const string Authorization = "Authorization";
    public const string Validation = "Validation";
    public const string Idempotency = "Idempotency";
    public const string Handler = "Handler";
    public const string Transaction = "Transaction";
    public const string Audit = "Audit";
    public const string Telemetry = "Telemetry";
    public const string Response = "Response";

    /// <summary>
    /// Records the order in which pipeline stages were composed at startup;
    /// asserted against <see cref="CanonicalOrder"/> by the architecture test.
    /// </summary>
    public static IList<string> ComposedOrder { get; } = [];
}
