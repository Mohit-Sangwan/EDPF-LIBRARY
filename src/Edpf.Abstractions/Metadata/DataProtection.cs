using System;

namespace Edpf.Abstractions.Metadata;

/// <summary>
/// The protections a field's classification requires (Phase 05b).
/// </summary>
/// <remarks>
/// <para>
/// This flags enum is the hinge of the whole classification-driven
/// architecture. Every protection in the framework — encryption at rest,
/// redaction in diagnostics, audit of access, inclusion in a subject-access
/// export — is derived from a classification level, in one place, so that
/// adding a protected field is a *declaration* rather than an implementation.
/// </para>
/// <para>
/// Crucially it is derived from a <see cref="Primitives.DataClassificationLevel"/>,
/// not from a <see cref="Type"/>. A field defined by a customer at runtime has
/// no CLR type to reflect over, and the moment protection depends on
/// reflection, custom fields fall outside the safety model — which is exactly
/// where they must not be.
/// </para>
/// </remarks>
[Flags]
public enum DataProtectionRequirements
{
    /// <summary>No protection required.</summary>
    None = 0,

    /// <summary>The value must be encrypted at rest.</summary>
    EncryptAtRest = 1,

    /// <summary>The value must never appear in logs, traces, metrics or errors.</summary>
    RedactInDiagnostics = 2,

    /// <summary>Reads and writes of the value must be audited.</summary>
    AuditAccess = 4,

    /// <summary>The value must be included in a data-subject access export.</summary>
    IncludeInSubjectAccess = 8,

    /// <summary>The value must be erasable by crypto-shredding (ADR-006).</summary>
    ErasableByKeyDestruction = 16,

    /// <summary>The value must never be stored in its raw form.</summary>
    TokenizeNeverStoreRaw = 32,
}

/// <summary>
/// Resolves the protections a classification level requires.
/// </summary>
/// <remarks>
/// One implementation, consulted by every subsystem. If encryption asked one
/// question and redaction asked a different one, the two would drift and the
/// gap between them would be a disclosure.
/// </remarks>
public interface IDataProtectionPolicy
{
    /// <summary>
    /// Returns the protections <paramref name="level"/> requires.
    /// </summary>
    /// <param name="level">The classification level.</param>
    /// <returns>The required protections.</returns>
    DataProtectionRequirements For(Primitives.DataClassificationLevel level);
}
