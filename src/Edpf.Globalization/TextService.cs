using System;
using System.Globalization;
using System.Text;
using Edpf.Core.Guards;

namespace Edpf.Globalization;

/// <summary>
/// Culture-correct text handling (Phase 27 §"Text handling").
/// </summary>
/// <remarks>
/// <para>
/// **The Turkish-i problem.** In Turkish, uppercase <c>I</c> lowercases to
/// dotless <c>ı</c>, not <c>i</c>. So <c>"FILE".ToLower()</c> under a Turkish
/// culture yields <c>"fıle"</c>, and a naive case-insensitive comparison
/// against <c>"file"</c> fails. The same defect has produced authentication
/// bypasses, failed config lookups and mis-parsed identifiers for decades —
/// it is the reason <c>ToLower()</c> for comparison is always wrong.
/// </para>
/// <para>
/// The rule this type enforces: **comparison for identity is ordinal;
/// comparison for a human is cultural.** Never the reverse, and never
/// <c>ToLower()</c> for either.
/// </para>
/// </remarks>
public static class TextService
{
    /// <summary>
    /// Compares two identifiers for equality — a code, a key, a header name,
    /// a filename.
    /// </summary>
    /// <param name="left">First value.</param>
    /// <param name="right">Second value.</param>
    /// <param name="ignoreCase">True for case-insensitive comparison.</param>
    /// <returns>True when the values are the same identifier.</returns>
    /// <remarks>
    /// Ordinal always. An identifier's equality must not depend on the
    /// thread's culture, or the same code behaves differently in Istanbul.
    /// </remarks>
    public static bool IdentifierEquals(string? left, string? right, bool ignoreCase = true)
        => string.Equals(
            left,
            right,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Compares two human-readable strings for display ordering, using the
    /// culture's collation.
    /// </summary>
    /// <param name="left">First value.</param>
    /// <param name="right">Second value.</param>
    /// <param name="culture">The culture whose collation applies.</param>
    /// <returns>Negative, zero or positive per the culture's ordering.</returns>
    /// <remarks>
    /// Collation genuinely differs: in Swedish <c>ä</c> sorts after <c>z</c>,
    /// while in German it sorts with <c>a</c>. A patient list sorted
    /// ordinally is wrong in both.
    /// </remarks>
    public static int CompareForDisplay(string? left, string? right, CultureInfo culture)
    {
        Guard.NotNull(culture, nameof(culture));
        return string.Compare(left, right, culture, CompareOptions.None);
    }

    /// <summary>
    /// Normalises text to Unicode NFC before storage or comparison.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The NFC-normalised text.</returns>
    /// <remarks>
    /// "é" can be one code point or two (e + combining acute). Both render
    /// identically and neither is equal to the other ordinally, so a name
    /// stored one way is not found when searched the other. Normalising on
    /// the way in makes the comparison meaningful.
    /// </remarks>
    public static string NormalizeForStorage(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value!.Normalize(NormalizationForm.FormC);

    /// <summary>
    /// Uppercases invariantly, for building a machine-readable key.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The invariant-uppercase text.</returns>
    /// <remarks>
    /// Invariant, never culture-sensitive: <c>"i".ToUpper()</c> under Turkish
    /// gives <c>İ</c> (dotted capital), so a culture-sensitive key derived
    /// from user text differs by deployment region.
    /// </remarks>
    public static string ToKey(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value!.ToUpperInvariant();
}
