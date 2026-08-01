using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Compliance;

/// <summary>
/// The lawful basis for a processing operation (GDPR Art. 6). Consent is one
/// basis among six, and treating it as the only one is a common and expensive
/// design error — emergency treatment proceeds on vital interests, not on a
/// signature.
/// </summary>
public enum LawfulBasis
{
    /// <summary>No basis established. Processing must not proceed.</summary>
    None = 0,

    /// <summary>Art. 6(1)(a) — the subject consented to this purpose.</summary>
    Consent = 1,

    /// <summary>Art. 6(1)(b) — necessary to perform a contract with the subject.</summary>
    Contract = 2,

    /// <summary>Art. 6(1)(c) — required by law, e.g. statutory clinical reporting.</summary>
    LegalObligation = 3,

    /// <summary>Art. 6(1)(d) — necessary to protect someone's life. The break-glass basis.</summary>
    VitalInterests = 4,

    /// <summary>Art. 6(1)(e) — a task in the public interest.</summary>
    PublicTask = 5,

    /// <summary>Art. 6(1)(f) — legitimate interests, subject to a balancing test.</summary>
    LegitimateInterests = 6,
}

/// <summary>
/// Decides whether a processing operation may proceed (Phase 22 §"Consent
/// management").
/// </summary>
/// <remarks>
/// **A processing operation without a lawful basis fails rather than
/// proceeds.** That is the whole design: consent checking that returns a
/// warning is decoration, because the operation happens anyway.
/// </remarks>
public interface IConsentEvaluator
{
    /// <summary>
    /// Evaluates whether processing may proceed.
    /// </summary>
    /// <param name="request">Who, what data, and for what purpose.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>
    /// The decision. A refusal carries
    /// <see cref="ErrorCodes.ConsentRequired"/> and names the purpose so the
    /// caller can request consent — but nothing about the subject.
    /// </returns>
    Task<Result<ConsentDecision>> EvaluateAsync(
        ProcessingRequest request, CancellationToken cancellationToken);
}

/// <summary>A proposed processing operation.</summary>
public sealed class ProcessingRequest
{
    /// <summary>
    /// Initializes a request.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subjectToken">The pseudonymous subject — never a raw identifier.</param>
    /// <param name="purpose">The declared purpose, e.g. <c>direct-care</c>, <c>research</c>.</param>
    /// <param name="dataCategories">Which classifications are involved.</param>
    /// <exception cref="ArgumentException">The tenant is empty or a string argument is blank.</exception>
    public ProcessingRequest(
        Guid tenantId,
        string subjectToken,
        string purpose,
        IReadOnlyList<DataClassificationLevel> dataCategories)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Processing requires a tenant.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            throw new ArgumentException("Processing requires a subject token.", nameof(subjectToken));
        }

        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException(
                "Processing requires a declared purpose; 'because we can' is not a purpose.", nameof(purpose));
        }

        TenantId = tenantId;
        SubjectToken = subjectToken;
        Purpose = purpose;
        DataCategories = dataCategories ?? throw new ArgumentNullException(nameof(dataCategories));
    }

    /// <summary>The tenant.</summary>
    public Guid TenantId { get; }

    /// <summary>The pseudonymous subject.</summary>
    public string SubjectToken { get; }

    /// <summary>The declared purpose.</summary>
    public string Purpose { get; }

    /// <summary>Which classifications are involved.</summary>
    public IReadOnlyList<DataClassificationLevel> DataCategories { get; }
}

/// <summary>The outcome of a consent evaluation, and its evidence.</summary>
public sealed class ConsentDecision
{
    private ConsentDecision(bool permitted, LawfulBasis basis, string? consentVersion, string reason)
    {
        IsPermitted = permitted;
        Basis = basis;
        ConsentVersion = consentVersion;
        Reason = reason;
    }

    /// <summary>True when processing may proceed.</summary>
    public bool IsPermitted { get; }

    /// <summary>The basis relied on. <see cref="LawfulBasis.None"/> when refused.</summary>
    public LawfulBasis Basis { get; }

    /// <summary>
    /// The version of the consent text relied on, when the basis is consent.
    /// Versioning matters: consent to v3 is not consent to v4's broader scope.
    /// </summary>
    public string? ConsentVersion { get; }

    /// <summary>Why, for the audit trail. Not returned to the caller.</summary>
    public string Reason { get; }

    /// <summary>Permits processing on a stated basis.</summary>
    /// <param name="basis">The lawful basis relied on.</param>
    /// <param name="reason">Why, for the audit trail.</param>
    /// <param name="consentVersion">The consent version, when the basis is consent.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="basis"/> is <see cref="LawfulBasis.None"/> — permitting
    /// with no basis is the exact outcome this type exists to prevent.
    /// </exception>
    public static ConsentDecision Permit(LawfulBasis basis, string reason, string? consentVersion = null)
    {
        if (basis == LawfulBasis.None)
        {
            throw new ArgumentException(
                "Processing cannot be permitted with no lawful basis.", nameof(basis));
        }

        return new ConsentDecision(true, basis, consentVersion, reason);
    }

    /// <summary>Refuses processing.</summary>
    /// <param name="reason">Why, for the audit trail.</param>
    public static ConsentDecision Refuse(string reason)
        => new(false, LawfulBasis.None, null, reason);
}

/// <summary>
/// Blocks destruction while a hold is in force (Phase 19/22). A hold
/// outranks both a retention schedule and an erasure request.
/// </summary>
public interface ILegalHoldStore
{
    /// <summary>
    /// Checks whether a hold blocks destruction of a subject's data.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subjectToken">The pseudonymous subject.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The blocking hold, or null when destruction may proceed.</returns>
    Task<LegalHold?> FindActiveHoldAsync(
        Guid tenantId, string subjectToken, CancellationToken cancellationToken);
}

/// <summary>A hold preventing destruction.</summary>
public sealed class LegalHold
{
    /// <summary>
    /// Initializes a hold.
    /// </summary>
    /// <param name="holdReference">The hold's reference, surfaced in refusals.</param>
    /// <param name="justification">Why it exists. Mandatory and audited.</param>
    /// <param name="placedUtc">When it was placed.</param>
    /// <param name="expiresUtc">When it lapses. Holds are time-bounded, not perpetual.</param>
    /// <exception cref="ArgumentException">The reference or justification is blank.</exception>
    public LegalHold(
        string holdReference, string justification, DateTimeOffset placedUtc, DateTimeOffset expiresUtc)
    {
        if (string.IsNullOrWhiteSpace(holdReference))
        {
            throw new ArgumentException("A hold requires a reference.", nameof(holdReference));
        }

        if (string.IsNullOrWhiteSpace(justification))
        {
            throw new ArgumentException(
                "A hold requires a written justification; it overrides a data subject's erasure right and must "
                + "be defensible.",
                nameof(justification));
        }

        HoldReference = holdReference;
        Justification = justification;
        PlacedUtc = placedUtc;
        ExpiresUtc = expiresUtc;
    }

    /// <summary>The hold's reference. Safe to return in a refusal.</summary>
    public string HoldReference { get; }

    /// <summary>Why the hold exists.</summary>
    public string Justification { get; }

    /// <summary>When it was placed.</summary>
    public DateTimeOffset PlacedUtc { get; }

    /// <summary>When it lapses.</summary>
    public DateTimeOffset ExpiresUtc { get; }

    /// <summary>True while the hold still blocks destruction.</summary>
    /// <param name="now">The current instant, from <see cref="IClock"/>.</param>
    public bool IsActive(DateTimeOffset now) => now < ExpiresUtc;
}
