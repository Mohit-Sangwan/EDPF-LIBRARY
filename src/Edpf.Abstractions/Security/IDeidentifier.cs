using System;
using System.Collections.Generic;

namespace Edpf.Abstractions.Security;

/// <summary>
/// The eighteen HIPAA Safe Harbor identifiers (45 CFR §164.514(b)(2)).
/// De-identification under Safe Harbor requires removing **all** of them; the
/// enum exists so coverage is enumerable and testable rather than asserted.
/// </summary>
public enum SafeHarborIdentifier
{
    /// <summary>
    /// The field holds no Safe Harbor identifier and passes through
    /// unchanged. Distinct from *unmapped*: this is a positive statement that
    /// someone classified the field and found it safe, which is exactly the
    /// evidence an auditor wants.
    /// </summary>
    None = 0,

    /// <summary>(A) Names.</summary>
    Name = 1,

    /// <summary>(B) Geographic subdivisions smaller than a state, including ZIP beyond the first three digits.</summary>
    GeographicSubdivision = 2,

    /// <summary>(C) All date elements except year, and any age over 89.</summary>
    DateElement = 3,

    /// <summary>(D) Telephone numbers.</summary>
    TelephoneNumber = 4,

    /// <summary>(E) Fax numbers.</summary>
    FaxNumber = 5,

    /// <summary>(F) Email addresses.</summary>
    EmailAddress = 6,

    /// <summary>(G) Social security numbers.</summary>
    SocialSecurityNumber = 7,

    /// <summary>(H) Medical record numbers.</summary>
    MedicalRecordNumber = 8,

    /// <summary>(I) Health plan beneficiary numbers.</summary>
    HealthPlanBeneficiaryNumber = 9,

    /// <summary>(J) Account numbers.</summary>
    AccountNumber = 10,

    /// <summary>(K) Certificate and licence numbers.</summary>
    CertificateOrLicenceNumber = 11,

    /// <summary>(L) Vehicle identifiers and serial numbers, including plates.</summary>
    VehicleIdentifier = 12,

    /// <summary>(M) Device identifiers and serial numbers.</summary>
    DeviceIdentifier = 13,

    /// <summary>(N) Web URLs.</summary>
    WebUrl = 14,

    /// <summary>(O) IP addresses.</summary>
    IpAddress = 15,

    /// <summary>(P) Biometric identifiers, including finger and voice prints.</summary>
    BiometricIdentifier = 16,

    /// <summary>(Q) Full-face photographs and comparable images.</summary>
    FullFacePhotograph = 17,

    /// <summary>(R) Any other unique identifying number, characteristic or code.</summary>
    OtherUniqueIdentifier = 18,
}

/// <summary>
/// Removes or transforms identifiers so data may be used for secondary
/// purposes (Phase 20 §"De-identification").
/// </summary>
/// <remarks>
/// De-identification is not encryption and must not be confused with it: the
/// output is intended to be readable, and its safety rests on the absence of
/// identifiers rather than on a key. That is why the verification is a
/// **re-identification attempt**, not a round-trip test.
/// </remarks>
public interface IDeidentifier
{
    /// <summary>
    /// Applies HIPAA Safe Harbor to a record.
    /// </summary>
    /// <param name="values">Field name → value.</param>
    /// <param name="policy">Which field maps to which identifier category.</param>
    /// <returns>
    /// The de-identified record, plus a report of what was removed — the
    /// report is the evidence an auditor asks for.
    /// </returns>
    DeidentificationResult ApplySafeHarbor(
        IReadOnlyDictionary<string, object?> values, SafeHarborPolicy policy);

    /// <summary>
    /// Shifts a date by a per-subject constant offset, preserving intervals
    /// between a subject's events while breaking absolute correlation.
    /// </summary>
    /// <param name="date">The date to shift. Time components are ignored.</param>
    /// <param name="subjectToken">The pseudonymous subject; determines the offset.</param>
    /// <returns>The shifted date.</returns>
    /// <remarks>
    /// <para>
    /// Consistency per subject is the point: a random shift per date would
    /// destroy "the fever started three days before admission", which is
    /// usually the clinically interesting fact.
    /// </para>
    /// <para>
    /// Typed as <see cref="DateTime"/> rather than <c>DateOnly</c> because
    /// <c>DateOnly</c> does not exist on Tier 3 TFMs (ADR-002) and
    /// <c>Edpf.Abstractions</c> may not polyfill (EDPF0001).
    /// </para>
    /// </remarks>
    DateTime ShiftDate(DateTime date, string subjectToken);
}

/// <summary>Maps a record's fields to Safe Harbor identifier categories.</summary>
public sealed class SafeHarborPolicy
{
    /// <summary>
    /// Initializes a policy.
    /// </summary>
    /// <param name="fieldIdentifiers">Field name → the identifier category it holds.</param>
    /// <param name="subjectTokenField">
    /// The field holding the pseudonymous subject token, used for consistent
    /// date shifting. Retained in the output; it is not an identifier.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentifiers"/> is null.</exception>
    /// <param name="rejectUnmappedFields">
    /// True (the default) to remove a field with no policy entry rather than
    /// pass it through. Safe Harbor requires the absence of all eighteen
    /// categories, and a field nobody classified is a field nobody checked.
    /// </param>
    public SafeHarborPolicy(
        IReadOnlyDictionary<string, SafeHarborIdentifier> fieldIdentifiers,
        string? subjectTokenField = null,
        bool rejectUnmappedFields = true)
    {
        FieldIdentifiers = fieldIdentifiers ?? throw new ArgumentNullException(nameof(fieldIdentifiers));
        SubjectTokenField = subjectTokenField;
        RejectUnmappedFields = rejectUnmappedFields;
    }

    /// <summary>Field name → identifier category.</summary>
    public IReadOnlyDictionary<string, SafeHarborIdentifier> FieldIdentifiers { get; }

    /// <summary>The field holding the subject token, if any.</summary>
    public string? SubjectTokenField { get; }

    /// <summary>
    /// True to fail closed on an unmapped field rather than passing it
    /// through. **Default true**: Safe Harbor requires the absence of all
    /// eighteen categories, and a field nobody classified is a field nobody
    /// checked.
    /// </summary>
    public bool RejectUnmappedFields { get; }
}

/// <summary>The outcome of a de-identification, with its evidence.</summary>
public sealed class DeidentificationResult
{
    /// <summary>
    /// Initializes a result.
    /// </summary>
    /// <param name="values">The de-identified record.</param>
    /// <param name="removedIdentifiers">Which categories were found and removed.</param>
    /// <param name="unmappedFields">Fields with no policy entry.</param>
    public DeidentificationResult(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyCollection<SafeHarborIdentifier> removedIdentifiers,
        IReadOnlyCollection<string> unmappedFields)
    {
        Values = values ?? throw new ArgumentNullException(nameof(values));
        RemovedIdentifiers = removedIdentifiers ?? throw new ArgumentNullException(nameof(removedIdentifiers));
        UnmappedFields = unmappedFields ?? throw new ArgumentNullException(nameof(unmappedFields));
    }

    /// <summary>The de-identified record.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>Identifier categories found and removed. Auditor evidence.</summary>
    public IReadOnlyCollection<SafeHarborIdentifier> RemovedIdentifiers { get; }

    /// <summary>
    /// Fields with no policy entry. Non-empty means the policy is incomplete,
    /// which is a finding rather than a detail.
    /// </summary>
    public IReadOnlyCollection<string> UnmappedFields { get; }

    /// <summary>True when every field was classified and handled.</summary>
    public bool IsComplete => UnmappedFields.Count == 0;
}
