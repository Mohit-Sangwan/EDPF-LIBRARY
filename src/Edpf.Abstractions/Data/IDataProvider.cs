using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Data;

/// <summary>
/// A supported database engine (Phase 06 §④). Implementations are certified by
/// passing the conformance suite in full (Z.12) — that suite, not this
/// interface, is what makes "database-agnostic" a testable claim.
/// </summary>
public interface IDataProvider
{
    /// <summary>Stable provider name, e.g. <c>SqlServer</c>, <c>PostgreSql</c>.</summary>
    string Name { get; }

    /// <summary>Honestly declared capabilities (ADR-016).</summary>
    IProviderCapabilities Capabilities { get; }

    /// <summary>This engine's SQL surface.</summary>
    ISqlDialect Dialect { get; }

    /// <summary>.NET ↔ engine type mapping.</summary>
    ITypeMapper TypeMapper { get; }

    /// <summary>
    /// Translates a native engine error into the EDPF taxonomy (§10.2) so
    /// callers handle one set of codes across thirteen engines. The mapping is
    /// part of the conformance suite.
    /// </summary>
    /// <param name="nativeErrorCode">The provider-specific error number.</param>
    /// <param name="nativeMessage">The provider message. Never surfaced to callers — it can carry data.</param>
    /// <returns>The mapped EDPF error.</returns>
    Error TranslateError(int nativeErrorCode, string nativeMessage);

    /// <summary>
    /// True when an operation that failed with this native code may be safely
    /// retried.
    /// </summary>
    /// <param name="nativeErrorCode">The provider-specific error number.</param>
    /// <returns>
    /// True only for **provably** transient faults. An ambiguous outcome — a
    /// timeout on a non-idempotent write — is never transient: retrying it is
    /// data corruption, not resilience (ADR-017).
    /// </returns>
    bool IsTransient(int nativeErrorCode);
}

/// <summary>
/// Maps .NET types to engine types and back (Phase 06 §④). Round-trip
/// fidelity — <c>value → store → read → value</c> being identity — is proven
/// per provider by property-based tests.
/// </summary>
public interface ITypeMapper
{
    /// <summary>
    /// The engine type for a .NET type.
    /// </summary>
    /// <param name="clrType">The .NET type.</param>
    /// <returns>
    /// The engine type name, or failure with
    /// <see cref="ErrorCodes.CapabilityNotSupported"/> when this engine has no
    /// faithful representation — an explicit refusal beats silent precision
    /// loss.
    /// </returns>
    Result<string> ToEngineType(Type clrType);

    /// <summary>
    /// Converts a value read from the engine back to its .NET representation,
    /// handling the per-engine traps: <c>DateTimeOffset</c> on Oracle,
    /// <c>decimal</c> precision, <c>Guid</c> stored as
    /// <c>uniqueidentifier</c> / <c>RAW(16)</c> / <c>CHAR(36)</c>,
    /// <c>TimeSpan</c>, and BLOB thresholds.
    /// </summary>
    /// <param name="value">The raw value from the reader.</param>
    /// <param name="targetType">The .NET type expected.</param>
    /// <returns>The converted value, or a conversion failure.</returns>
    Result<object?> FromEngineValue(object? value, Type targetType);

    /// <summary>Every .NET type this provider round-trips faithfully.</summary>
    IReadOnlyCollection<Type> SupportedClrTypes { get; }
}

/// <summary>
/// Resolves providers by name (Phase 06 §④). Third parties register their own
/// implementations here and prove correctness by passing the public
/// conformance suite (Phase 06 §⑧).
/// </summary>
public interface IProviderRegistry
{
    /// <summary>
    /// Resolves a provider by name.
    /// </summary>
    /// <param name="providerName">The provider name, case-insensitive.</param>
    /// <returns>
    /// The provider, or failure with
    /// <see cref="ErrorCodes.ConfigurationInvalid"/> when it is not
    /// registered — misconfiguration fails at startup, not mid-request.
    /// </returns>
    Result<IDataProvider> Resolve(string providerName);

    /// <summary>All registered providers.</summary>
    IReadOnlyCollection<IDataProvider> All { get; }
}
