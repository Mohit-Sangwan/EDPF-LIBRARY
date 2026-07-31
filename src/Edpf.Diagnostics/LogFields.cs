namespace Edpf.Diagnostics;

/// <summary>
/// The standard structured-log schema (Phase 00 §⑥). Every log entry uses
/// these property names so queries work identically across services and sinks.
/// Log content rules (Z.10): no classified data, no secrets, no raw subject
/// identifiers — subject references appear only as tokens.
/// </summary>
public static class LogFields
{
    /// <summary>The correlation id spanning the logical operation.</summary>
    public const string CorrelationId = "CorrelationId";

    /// <summary>The id of the current request hop.</summary>
    public const string RequestId = "RequestId";

    /// <summary>The id of the causing request or message.</summary>
    public const string CausationId = "CausationId";

    /// <summary>The tenant id. A GUID — tenant ids are Internal, not PII.</summary>
    public const string TenantId = "TenantId";

    /// <summary>The stable EDPF error code, when logging a failure.</summary>
    public const string ErrorCode = "ErrorCode";

    /// <summary>The domain event type (§10.5), when applicable.</summary>
    public const string EventType = "EventType";

    /// <summary>The pseudonymous subject token — never a raw identifier (BRL-006).</summary>
    public const string SubjectToken = "SubjectToken";

    /// <summary>Operation duration in milliseconds.</summary>
    public const string DurationMs = "DurationMs";

    /// <summary>Operation outcome: <c>success</c> or <c>failure</c>.</summary>
    public const string Outcome = "Outcome";
}
