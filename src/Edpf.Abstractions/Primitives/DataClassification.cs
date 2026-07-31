using System;

namespace Edpf.Abstractions.Primitives;

/// <summary>
/// The data-classification scheme (Phase 00 deliverable 4). Every level
/// carries handling rules — at rest, in transit, in logs, in memory, in
/// exports — defined in <c>docs/compliance/data-classification.md</c>.
/// </summary>
public enum DataClassificationLevel
{
    /// <summary>Freely disclosable.</summary>
    Public = 0,

    /// <summary>Internal business data; not for external disclosure.</summary>
    Internal = 1,

    /// <summary>Commercially sensitive; encrypted at rest.</summary>
    Confidential = 2,

    /// <summary>Personally identifiable information (GDPR/DPDP). Encrypted; never logged.</summary>
    Pii = 3,

    /// <summary>
    /// Protected health information (HIPAA §164). Field-level encryption under
    /// a per-subject DEK (ADR-006); never logged; tokenized in audit and events.
    /// </summary>
    Phi = 4,

    /// <summary>Payment card data (PCI DSS). Tokenized; never stored raw.</summary>
    Pci = 5,
}

/// <summary>
/// Tags a type or member with its data classification, making PII/PHI
/// machine-discoverable from the very first entity (Phase 01 §⑥) — this is
/// what makes the Phase 23 classifier and Phase 22 DSAR tooling feasible,
/// and what rule EDPF0005 (never log a classified member) enforces against.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true)]
public sealed class DataClassificationAttribute : Attribute
{
    /// <summary>Tags the target with <paramref name="level"/>.</summary>
    /// <param name="level">The classification level.</param>
    public DataClassificationAttribute(DataClassificationLevel level) => Level = level;

    /// <summary>The classification level of the tagged type or member.</summary>
    public DataClassificationLevel Level { get; }
}
