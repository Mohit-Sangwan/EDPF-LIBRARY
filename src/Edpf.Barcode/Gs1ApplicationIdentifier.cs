using System;
using System.Collections.Generic;

namespace Edpf.Barcode;

/// <summary>
/// A GS1 Application Identifier — what the digits that follow it mean, and how
/// long they are (Phase 17c).
/// </summary>
/// <remarks>
/// <para>
/// **Whether an AI is fixed- or variable-length is the whole safety
/// question.** A fixed-length field ends where the standard says it ends. A
/// variable-length field ends at a separator, and if that separator is missing
/// the next field is read as a continuation of this one.
/// </para>
/// <para>
/// Concretely: <c>(10)</c> lot number is variable-length. Encode lot
/// <c>ABC</c> followed by expiry <c>(17)260801</c> without a separator, and a
/// scanner reads the lot as <c>ABC17260801</c> and finds no expiry at all —
/// so a medication past its expiry date scans as one that has none.
/// </para>
/// </remarks>
public sealed class Gs1ApplicationIdentifier
{
    private Gs1ApplicationIdentifier(
        string ai, string name, int fixedLength, int maxLength, bool numericOnly)
    {
        Ai = ai;
        Name = name;
        FixedLength = fixedLength;
        MaxLength = maxLength;
        NumericOnly = numericOnly;
    }

    /// <summary>The AI digits.</summary>
    public string Ai { get; }

    /// <summary>What the field means.</summary>
    public string Name { get; }

    /// <summary>The exact data length, or 0 when variable.</summary>
    public int FixedLength { get; }

    /// <summary>The maximum data length.</summary>
    public int MaxLength { get; }

    /// <summary>Whether the data must be digits only.</summary>
    public bool NumericOnly { get; }

    /// <summary>
    /// True when this field needs a separator before whatever follows it.
    /// </summary>
    public bool IsVariableLength => FixedLength == 0;

    /// <summary>
    /// The identifiers this framework encodes and decodes.
    /// </summary>
    /// <remarks>
    /// A deliberately small set, covering the healthcare traceability fields
    /// the phase names. An unknown AI is refused rather than passed through:
    /// passing it through means guessing its length, and guessing the length
    /// of a variable-length field is exactly the failure described above.
    /// </remarks>
    public static IReadOnlyDictionary<string, Gs1ApplicationIdentifier> Known { get; } =
        new Dictionary<string, Gs1ApplicationIdentifier>(StringComparer.Ordinal)
        {
            ["00"] = new("00", "Serial Shipping Container Code", 18, 18, true),
            ["01"] = new("01", "Global Trade Item Number", 14, 14, true),
            ["10"] = new("10", "Batch or lot number", 0, 20, false),
            ["11"] = new("11", "Production date (YYMMDD)", 6, 6, true),
            ["17"] = new("17", "Expiration date (YYMMDD)", 6, 6, true),
            ["21"] = new("21", "Serial number", 0, 20, false),
            ["30"] = new("30", "Variable count", 0, 8, true),
            ["240"] = new("240", "Additional product identification", 0, 30, false),
            ["241"] = new("241", "Customer part number", 0, 30, false),
            ["251"] = new("251", "Reference to source entity", 0, 30, false),
            ["8018"] = new("8018", "Global Service Relation Number — recipient", 18, 18, true),
        };

    /// <summary>
    /// Looks up an identifier.
    /// </summary>
    /// <param name="ai">The AI digits.</param>
    /// <returns>The identifier, or <see langword="null"/> when unknown.</returns>
    public static Gs1ApplicationIdentifier? Find(string? ai)
        => ai is not null && Known.TryGetValue(ai, out Gs1ApplicationIdentifier? found) ? found : null;
}
