using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.DataPlatform.Classification;

/// <summary>What a classifier found in a value.</summary>
public sealed class ClassificationFinding
{
    /// <summary>
    /// Initializes a finding.
    /// </summary>
    /// <param name="fieldName">Where it was found.</param>
    /// <param name="detectedKind">What it looks like.</param>
    /// <param name="suggestedLevel">The classification the field should carry.</param>
    /// <param name="checkDigitValid">
    /// True when a check digit confirmed the format. A pattern match without
    /// a valid check digit is a weak signal and is reported as such rather
    /// than suppressed.
    /// </param>
    public ClassificationFinding(
        string fieldName,
        SensitiveDataKind detectedKind,
        DataClassificationLevel suggestedLevel,
        bool checkDigitValid)
    {
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        DetectedKind = detectedKind;
        SuggestedLevel = suggestedLevel;
        CheckDigitValid = checkDigitValid;
    }

    /// <summary>Where it was found.</summary>
    public string FieldName { get; }

    /// <summary>What it looks like.</summary>
    public SensitiveDataKind DetectedKind { get; }

    /// <summary>The classification the field should carry.</summary>
    public DataClassificationLevel SuggestedLevel { get; }

    /// <summary>True when a check digit confirmed the format.</summary>
    public bool CheckDigitValid { get; }

    /// <summary>
    /// A finding worth blocking a merge on. Check-digit-confirmed matches
    /// only: a bare pattern match is reported for review but does not stop
    /// the build, because a classifier that cries wolf gets switched off.
    /// </summary>
    public bool IsHighConfidence => CheckDigitValid;

    /// <summary>Describes the finding by field and kind. Carries no value.</summary>
    public override string ToString() => FieldName + ": looks like " + DetectedKind;
}

/// <summary>Kinds of sensitive data the classifier recognises.</summary>
public enum SensitiveDataKind
{
    /// <summary>Nothing sensitive detected.</summary>
    None = 0,

    /// <summary>US Social Security number.</summary>
    SocialSecurityNumber = 1,

    /// <summary>Payment card number (Luhn-validated).</summary>
    PaymentCard = 2,

    /// <summary>UK NHS number (mod-11).</summary>
    NhsNumber = 3,

    /// <summary>India Aadhaar number (Verhoeff).</summary>
    Aadhaar = 4,

    /// <summary>Email address.</summary>
    EmailAddress = 5,

    /// <summary>A medical record number in a recognised local format.</summary>
    MedicalRecordNumber = 6,

    /// <summary>An IP address.</summary>
    IpAddress = 7,
}

/// <summary>
/// Detects sensitive data in values and, more importantly, detects
/// **classification drift**: a field carrying sensitive data that nobody
/// declared (Phase 23).
/// </summary>
/// <remarks>
/// <para>
/// The drift case is the one that matters. Phase 01 made classification
/// declarative so that encryption, redaction, audit and export controls all
/// follow automatically — which means a developer who adds an unmarked
/// <c>PatientNotes</c> column silently opts that column out of every one of
/// them. This classifier is the detector for that, and it is why the finding
/// blocks a merge rather than filing a ticket.
/// </para>
/// <para>
/// Precision is bought with check digits (see
/// <see cref="IdentifierValidators"/>). A classifier with poor precision gets
/// muted within a week, and a muted classifier detects nothing.
/// </para>
/// </remarks>
public sealed class DataClassifier
{
    private static readonly Regex SsnPatternInstance = new(@"\b\d{3}-?\d{2}-?\d{4}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex SsnPattern() => SsnPatternInstance;

    private static readonly Regex CardPatternInstance = new(@"\b(?:\d[ -]?){12,18}\d\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex CardPattern() => CardPatternInstance;

    private static readonly Regex NhsPatternInstance = new(@"\b\d{3}[ -]?\d{3}[ -]?\d{4}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex NhsPattern() => NhsPatternInstance;

    private static readonly Regex AadhaarPatternInstance = new(@"\b\d{4}[ -]?\d{4}[ -]?\d{4}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex AadhaarPattern() => AadhaarPatternInstance;

    private static readonly Regex EmailPatternInstance = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex EmailPattern() => EmailPatternInstance;

    private static readonly Regex IpPatternInstance = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex IpPattern() => IpPatternInstance;

    private static readonly Regex MrnPatternInstance = new(
        @"\bMRN[- ]?[A-Z0-9]{4,20}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex MrnPattern() => MrnPatternInstance;

    /// <summary>
    /// Classifies one value.
    /// </summary>
    /// <param name="fieldName">The field being examined.</param>
    /// <param name="value">The value.</param>
    /// <returns>The finding, or null when nothing sensitive was detected.</returns>
    public static ClassificationFinding? Classify(string fieldName, string? value)
    {
        Guard.NotNullOrWhiteSpace(fieldName, nameof(fieldName));

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Ordered most specific first: an Aadhaar number also matches the
        // loose card pattern, and reporting it as a payment card would send
        // the finding to the wrong team.
        if (AadhaarPattern().IsMatch(value))
        {
            string digits = IdentifierValidators.DigitsOnly(value);
            if (IdentifierValidators.IsValidAadhaar(digits))
            {
                return new ClassificationFinding(
                    fieldName, SensitiveDataKind.Aadhaar, DataClassificationLevel.Pii, true);
            }
        }

        if (NhsPattern().IsMatch(value))
        {
            string digits = IdentifierValidators.DigitsOnly(value);
            if (IdentifierValidators.IsValidNhsNumber(digits))
            {
                return new ClassificationFinding(
                    fieldName, SensitiveDataKind.NhsNumber, DataClassificationLevel.Phi, true);
            }
        }

        if (SsnPattern().IsMatch(value))
        {
            string digits = IdentifierValidators.DigitsOnly(value);
            if (digits.Length == 9)
            {
                return new ClassificationFinding(
                    fieldName,
                    SensitiveDataKind.SocialSecurityNumber,
                    DataClassificationLevel.Pii,
                    IdentifierValidators.IsStructurallyValidSsn(digits));
            }
        }

        if (CardPattern().IsMatch(value))
        {
            string digits = IdentifierValidators.DigitsOnly(value);
            if (IdentifierValidators.IsValidLuhn(digits))
            {
                return new ClassificationFinding(
                    fieldName, SensitiveDataKind.PaymentCard, DataClassificationLevel.Pci, true);
            }
        }

        if (MrnPattern().IsMatch(value))
        {
            return new ClassificationFinding(
                fieldName, SensitiveDataKind.MedicalRecordNumber, DataClassificationLevel.Phi, false);
        }

        if (EmailPattern().IsMatch(value))
        {
            return new ClassificationFinding(
                fieldName, SensitiveDataKind.EmailAddress, DataClassificationLevel.Pii, false);
        }

        if (IpPattern().IsMatch(value))
        {
            return new ClassificationFinding(
                fieldName, SensitiveDataKind.IpAddress, DataClassificationLevel.Pii, false);
        }

        return null;
    }

    /// <summary>
    /// Detects classification drift: fields whose sampled values look
    /// sensitive but whose declared classification does not say so.
    /// </summary>
    /// <param name="samples">Field name → sampled values.</param>
    /// <param name="declared">Field name → the classification declared in code.</param>
    /// <returns>
    /// Findings for fields that are under-classified. A field declared at or
    /// above the suggested level is not reported, so tightening a
    /// classification never produces noise.
    /// </returns>
    public static IReadOnlyList<ClassificationFinding> DetectDrift(
        IReadOnlyDictionary<string, IReadOnlyList<string>> samples,
        IReadOnlyDictionary<string, DataClassificationLevel> declared)
    {
        Guard.NotNull(samples, nameof(samples));
        Guard.NotNull(declared, nameof(declared));

        var drift = new List<ClassificationFinding>();

        foreach (KeyValuePair<string, IReadOnlyList<string>> field in samples)
        {
            DataClassificationLevel declaredLevel =
                declared.TryGetValue(field.Key, out DataClassificationLevel level)
                    ? level
                    : DataClassificationLevel.Public;

            foreach (string value in field.Value)
            {
                ClassificationFinding? finding = Classify(field.Key, value);

                if (finding is not null && finding.SuggestedLevel > declaredLevel)
                {
                    drift.Add(finding);
                    break; // One finding per field is enough to act on.
                }
            }
        }

        return drift;
    }
}
