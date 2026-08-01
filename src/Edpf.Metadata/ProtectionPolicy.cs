using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;

namespace Edpf.Metadata;

/// <summary>
/// Maps a classification level to the protections it requires (Phase 05b).
/// </summary>
/// <remarks>
/// <para>
/// **This is the single table the whole classification-driven architecture
/// rests on.** Encryption, redaction, audit and subject-access export all
/// consult it; none of them decides for itself. If they decided separately,
/// the four would drift, and the gap between any two of them would be a
/// disclosure that no single subsystem's tests would catch.
/// </para>
/// <para>
/// Handling rules per level are specified in
/// <c>docs/compliance/data-classification.md</c>; this type is that document
/// made executable.
/// </para>
/// </remarks>
public sealed class ProtectionPolicy : IDataProtectionPolicy
{
    /// <summary>The policy corresponding to the published classification scheme.</summary>
    public static ProtectionPolicy Default { get; } = new();

    /// <inheritdoc />
    public DataProtectionRequirements For(DataClassificationLevel level) => level switch
    {
        DataClassificationLevel.Public => DataProtectionRequirements.None,

        // Not disclosable externally, but not personal: no erasure obligation
        // and no subject-access claim attaches to it.
        //
        // Deliberately NOT redacted from diagnostics, matching the ADR-015
        // redactor's threshold of Confidential-and-above. Redacting Internal
        // would redact almost everything — the compiled scanner defaults
        // untagged properties to Internal — and a log where every field reads
        // [REDACTED] is a log nobody can operate from. Engineers respond to
        // that by logging around the redactor, which is strictly worse than
        // the exposure it was meant to prevent.
        DataClassificationLevel.Internal => DataProtectionRequirements.None,

        DataClassificationLevel.Confidential =>
            DataProtectionRequirements.EncryptAtRest
            | DataProtectionRequirements.RedactInDiagnostics
            | DataProtectionRequirements.AuditAccess,

        // Personal data: the subject can demand a copy (GDPR Art. 15) and
        // erasure (Art. 17), and ADR-006 satisfies erasure by destroying the
        // key rather than the row, so the audit trail survives intact.
        DataClassificationLevel.Pii =>
            DataProtectionRequirements.EncryptAtRest
            | DataProtectionRequirements.RedactInDiagnostics
            | DataProtectionRequirements.AuditAccess
            | DataProtectionRequirements.IncludeInSubjectAccess
            | DataProtectionRequirements.ErasableByKeyDestruction,

        // PHI carries everything PII carries. HIPAA §164.312(b) additionally
        // requires the access record itself to be tamper-evident, which is the
        // audit chain's job rather than this table's.
        DataClassificationLevel.Phi =>
            DataProtectionRequirements.EncryptAtRest
            | DataProtectionRequirements.RedactInDiagnostics
            | DataProtectionRequirements.AuditAccess
            | DataProtectionRequirements.IncludeInSubjectAccess
            | DataProtectionRequirements.ErasableByKeyDestruction,

        // PCI DSS: the raw pan is never stored, so encryption at rest is not
        // the control — not holding it is.
        DataClassificationLevel.Pci =>
            DataProtectionRequirements.TokenizeNeverStoreRaw
            | DataProtectionRequirements.RedactInDiagnostics
            | DataProtectionRequirements.AuditAccess,

        // An unrecognised level is a newly added one, and defaulting to the
        // strongest available treatment is the only safe direction to fail:
        // under-protecting is a breach, over-protecting is an inconvenience.
        _ => DataProtectionRequirements.EncryptAtRest
            | DataProtectionRequirements.RedactInDiagnostics
            | DataProtectionRequirements.AuditAccess
            | DataProtectionRequirements.IncludeInSubjectAccess
            | DataProtectionRequirements.ErasableByKeyDestruction,
    };
}

/// <summary>Flag helpers that read as prose at the call site.</summary>
public static class DataProtectionRequirementsExtensions
{
    /// <summary>
    /// True when <paramref name="requirements"/> includes <paramref name="flag"/>.
    /// </summary>
    /// <param name="requirements">The requirement set.</param>
    /// <param name="flag">The flag to test.</param>
    /// <returns>Whether the flag is set.</returns>
    public static bool HasFlagSet(this DataProtectionRequirements requirements, DataProtectionRequirements flag)
        => (requirements & flag) == flag;
}
