namespace Edpf.Abstractions.Primitives;

/// <summary>
/// The stable EDPF error-code catalogue (§10.2). Codes follow
/// <c>EDPF-&lt;AREA&gt;-&lt;NNNN&gt;</c>, are public, and are stable forever (Z.2):
/// support and customers depend on them. New codes are appended; existing
/// codes are never renumbered or reused.
/// </summary>
public static class ErrorCodes
{
    /// <summary>Input failed validation. Detail: field + rule, never raw input.</summary>
    public const string ValidationFailed = "EDPF-VAL-1001";

    /// <summary>Payload schema mismatch. Detail: path only.</summary>
    public const string SchemaMismatch = "EDPF-VAL-1002";

    /// <summary>Credential invalid or expired. Detail: none — generic.</summary>
    public const string AuthenticationFailed = "EDPF-AUTH-2001";

    /// <summary>Multi-factor step-up required. Detail: challenge type.</summary>
    public const string MfaRequired = "EDPF-AUTH-2002";

    /// <summary>Permission denied. Detail: required permission only.</summary>
    public const string AuthorizationFailed = "EDPF-AUTHZ-2101";

    /// <summary>
    /// Cross-tenant access attempt. Surfaces as HTTP 404 — existence is not
    /// disclosed across a tenant boundary.
    /// </summary>
    public const string TenantScopeViolation = "EDPF-AUTHZ-2102";

    /// <summary>Field stripped from a projection by field-level authorization.</summary>
    public const string FieldAccessDenied = "EDPF-AUTHZ-2103";

    /// <summary>Optimistic concurrency conflict. Detail: current version token.</summary>
    public const string ConcurrencyConflict = "EDPF-DATA-3001";

    /// <summary>Entity absent. Detail: type + id.</summary>
    public const string NotFound = "EDPF-DATA-3002";

    /// <summary>Uniqueness violated. Detail: constraint name.</summary>
    public const string Duplicate = "EDPF-DATA-3003";

    /// <summary>Provider error. Detail: correlation id only.</summary>
    public const string ProviderFailure = "EDPF-DATA-3004";

    /// <summary>Capability unavailable on the active provider. Detail: capability name.</summary>
    public const string CapabilityNotSupported = "EDPF-DATA-3005";

    /// <summary>Field not filterable. Detail: field name if it exists and is readable.</summary>
    public const string InvalidFilter = "EDPF-QRY-3101";

    /// <summary>Estimated query cost exceeds budget. Detail: budget only.</summary>
    public const string QueryCostExceeded = "EDPF-QRY-3102";

    /// <summary>Transaction failed. Detail: correlation id.</summary>
    public const string TransactionFailed = "EDPF-TX-4001";

    /// <summary>Idempotency key reused with a different payload. Detail: key only.</summary>
    public const string IdempotencyConflict = "EDPF-TX-4002";

    /// <summary>Encrypt/decrypt failure. Detail: correlation id. Pages a human (§13.16).</summary>
    public const string CryptoFailure = "EDPF-SEC-5001";

    /// <summary>Key erased by crypto-shredding (ADR-006). Detail: erasure date. HTTP 410.</summary>
    public const string KeyDestroyed = "EDPF-SEC-5002";

    /// <summary>Rate or quota exceeded. Detail: Retry-After.</summary>
    public const string RateLimited = "EDPF-SEC-5003";

    /// <summary>No lawful basis for the requested purpose. Detail: purpose.</summary>
    public const string ConsentRequired = "EDPF-CMP-6001";

    /// <summary>Cross-region access refused (ADR-010). Detail: regions.</summary>
    public const string ResidencyViolation = "EDPF-CMP-6002";

    /// <summary>Operation blocked by legal hold. Detail: hold reference.</summary>
    public const string LegalHold = "EDPF-CMP-6003";

    /// <summary>
    /// The message's effective classification exceeds what the delivery channel
    /// may carry. Detail: channel name and the ceiling — never the content.
    /// </summary>
    /// <remarks>
    /// Unencrypted email and SMS traverse infrastructure the platform does not
    /// control, so they carry a ceiling below PHI. This is the code that fires
    /// when a template would have put clinical detail into one.
    /// </remarks>
    public const string ChannelClassificationExceeded = "EDPF-CMP-6004";

    /// <summary>Retryable dependency failure. Detail: Retry-After.</summary>
    public const string TransientFailure = "EDPF-INT-7001";

    /// <summary>Circuit breaker open. Detail: Retry-After.</summary>
    public const string CircuitOpen = "EDPF-INT-7002";

    /// <summary>Upstream integration failed. Detail: correlation id.</summary>
    public const string IntegrationFailed = "EDPF-INT-7003";

    /// <summary>Invalid configuration — fails at startup. Detail: key name, never value.</summary>
    public const string ConfigurationInvalid = "EDPF-CFG-8001";

    /// <summary>
    /// Audit subsystem unavailable. Critical: the platform must restore audit
    /// before serving (§13.16) — a failed audit fails the operation (BRL-005).
    /// </summary>
    public const string AuditUnavailable = "EDPF-AUDIT-9001";
}
