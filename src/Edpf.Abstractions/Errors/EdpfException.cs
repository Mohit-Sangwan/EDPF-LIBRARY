using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Errors;

/// <summary>
/// The root of the EDPF exception taxonomy (Phase 18). Every EDPF exception
/// carries an <see cref="Error"/> — a stable code, a safe message, and a
/// category — rather than free-text detail.
/// </summary>
/// <remarks>
/// <para>
/// **The critical security property (Phase 18):** an outward-facing error
/// reveals a correlation id and a stable code — never a stack trace, SQL
/// fragment, connection string, internal path, provider version, or a message
/// that distinguishes "does not exist" from "exists but you may not see it".
/// Full detail goes to the log, keyed by the correlation id. A support
/// engineer resolves the id to the whole story; an attacker gets nothing.
/// </para>
/// <para>
/// Because the message is derived from the catalogue rather than composed at
/// the throw site, these types are registered as **message-safe** with the
/// ADR-015 redactor — they are the exception to the rule that exception
/// messages are surrendered, and they earn it by construction.
/// </para>
/// </remarks>
public abstract class EdpfException : Exception
{
    /// <summary>
    /// Initializes an EDPF exception.
    /// </summary>
    /// <param name="error">The stable error this exception represents.</param>
    /// <param name="innerException">The underlying cause, if any. Never surfaced outward.</param>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    protected EdpfException(Error error, Exception? innerException = null)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Message, innerException)
        => Error = error;

    /// <summary>The stable error: code, safe message, category, correlation id.</summary>
    public Error Error { get; }

    /// <summary>The stable, public error code (§10.2).</summary>
    public string Code => Error.Code;

    /// <summary>
    /// Every type in the taxonomy, for registering with the redactor as
    /// message-safe (ADR-015).
    /// </summary>
    public static IReadOnlyList<Type> TaxonomyTypes { get; } =
    [
        typeof(EdpfValidationException),
        typeof(EdpfConcurrencyException),
        typeof(EdpfAuthorizationException),
        typeof(EdpfTenantScopeException),
        typeof(EdpfProviderException),
        typeof(EdpfCapabilityNotSupportedException),
        typeof(EdpfTransientException),
        typeof(EdpfComplianceException),
        typeof(EdpfCryptoException),
        typeof(EdpfKeyDestroyedException),
        typeof(EdpfNotFoundException),
    ];
}

/// <summary>Input failed validation (EDPF-VAL-1001). HTTP 400.</summary>
public sealed class EdpfValidationException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The validation error. Detail is field + rule, never raw input.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdpfValidationException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>Optimistic concurrency conflict (EDPF-DATA-3001). HTTP 409.</summary>
public sealed class EdpfConcurrencyException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The conflict error, carrying the current version token.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdpfConcurrencyException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>Permission denied within the caller's own tenant (EDPF-AUTHZ-2101). HTTP 403.</summary>
public sealed class EdpfAuthorizationException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The authorization error. Detail is the required permission only.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdpfAuthorizationException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>
/// Cross-tenant access (EDPF-AUTHZ-2102). Surfaces as **HTTP 404**, and is
/// deliberately indistinguishable from <see cref="EdpfNotFoundException"/> at
/// the wire — disclosing that a resource exists but belongs to someone else
/// is itself the leak.
/// </summary>
public sealed class EdpfTenantScopeException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The scope error. Detail: none — existence is not disclosed.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdpfTenantScopeException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>Entity absent (EDPF-DATA-3002). HTTP 404.</summary>
public sealed class EdpfNotFoundException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The not-found error.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdpfNotFoundException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>Provider failure (EDPF-DATA-3004). HTTP 500; detail is the correlation id only.</summary>
public sealed class EdpfProviderException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The provider error, already translated to the taxonomy.</param>
    /// <param name="innerException">The native driver exception. Logged, never surfaced.</param>
    public EdpfProviderException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>The active provider lacks a required capability (EDPF-DATA-3005). HTTP 501.</summary>
public sealed class EdpfCapabilityNotSupportedException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The capability error, naming the capability.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdpfCapabilityNotSupportedException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>Retryable dependency failure (EDPF-INT-7001). HTTP 503.</summary>
public sealed class EdpfTransientException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The transient error, carrying Retry-After.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdpfTransientException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>A compliance control refused the operation (EDPF-CMP-*).</summary>
public sealed class EdpfComplianceException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The compliance error — consent, residency or legal hold.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdpfComplianceException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>Encrypt or decrypt failure (EDPF-SEC-5001). Pages a human (§13.16).</summary>
public sealed class EdpfCryptoException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The crypto error. Detail: correlation id only.</param>
    /// <param name="innerException">The underlying cause, if any. Never surfaced.</param>
    public EdpfCryptoException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}

/// <summary>Key destroyed by crypto-shredding (EDPF-SEC-5002). HTTP 410.</summary>
public sealed class EdpfKeyDestroyedException : EdpfException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="error">The erasure error, carrying the erasure date.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdpfKeyDestroyedException(Error error, Exception? innerException = null)
        : base(error, innerException)
    {
    }
}
