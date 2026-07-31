namespace Edpf.Abstractions.Primitives;

/// <summary>
/// Coarse classification of an <see cref="Error"/>, used for transport mapping
/// (HTTP status, retry policy) without parsing error codes.
/// Aligned with the error and exception catalogue (§10.2).
/// </summary>
public enum ErrorCategory
{
    /// <summary>Input failed validation (EDPF-VAL-*). Maps to HTTP 400.</summary>
    Validation = 0,

    /// <summary>Credential invalid, expired or missing (EDPF-AUTH-*). Maps to HTTP 401.</summary>
    Authentication = 1,

    /// <summary>Permission denied within the caller's tenant (EDPF-AUTHZ-*). Maps to HTTP 403.</summary>
    Authorization = 2,

    /// <summary>
    /// Entity absent — or outside the caller's tenant scope, which is deliberately
    /// indistinguishable from absent (EDPF-AUTHZ-2102 maps to 404, never 403,
    /// because leaking existence is itself a leak).
    /// </summary>
    NotFound = 3,

    /// <summary>Uniqueness or state conflict (EDPF-DATA-3003, EDPF-TX-4002). Maps to HTTP 409.</summary>
    Conflict = 4,

    /// <summary>Optimistic concurrency conflict (EDPF-DATA-3001). Maps to HTTP 409.</summary>
    Concurrency = 5,

    /// <summary>Retryable dependency failure (EDPF-INT-7001/7002). Maps to HTTP 503.</summary>
    Transient = 6,

    /// <summary>Invalid configuration — fails at startup, never mid-request (EDPF-CFG-8001).</summary>
    Configuration = 7,

    /// <summary>Cryptographic or key-custody failure (EDPF-SEC-*).</summary>
    Security = 8,

    /// <summary>Compliance control refused the operation (EDPF-CMP-*).</summary>
    Compliance = 9,

    /// <summary>Upstream integration failed (EDPF-INT-7003). Maps to HTTP 502.</summary>
    Integration = 10,

    /// <summary>Unexpected internal failure. Detail stays in the log against the correlation id.</summary>
    Internal = 11,
}
