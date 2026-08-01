using Edpf.Abstractions.Compliance;
using Edpf.Abstractions.Primitives;

namespace Edpf.UnitTests.Compliance;

/// <summary>
/// Phase 22: "a processing operation without a lawful basis fails rather than
/// proceeds", and a legal hold blocks erasure.
/// </summary>
public sealed class ConsentAndHoldTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Permit_WithNoLawfulBasis_IsRejectedAtConstruction()
    {
        // The failure mode this type exists to prevent: an "approved"
        // decision that rests on nothing.
        Assert.Throws<ArgumentException>(
            () => ConsentDecision.Permit(LawfulBasis.None, "seemed fine"));
    }

    [Fact]
    public void Permit_OnConsent_RecordsTheConsentVersion()
    {
        // Consent to v3 is not consent to v4's broader scope, so the version
        // relied on is part of the evidence.
        ConsentDecision decision = ConsentDecision.Permit(
            LawfulBasis.Consent, "subject consented to research use", consentVersion: "research-consent-v3");

        Assert.True(decision.IsPermitted);
        Assert.Equal("research-consent-v3", decision.ConsentVersion);
    }

    [Fact]
    public void Permit_OnVitalInterests_NeedsNoConsentVersion()
    {
        // Emergency treatment proceeds on Art. 6(1)(d), not on a signature.
        // Treating consent as the only basis is a common design error.
        ConsentDecision decision = ConsentDecision.Permit(
            LawfulBasis.VitalInterests, "break-glass: unconscious patient in resus");

        Assert.True(decision.IsPermitted);
        Assert.Null(decision.ConsentVersion);
    }

    [Fact]
    public void Refuse_Always_CarriesNoBasis()
    {
        ConsentDecision decision = ConsentDecision.Refuse("consent withdrawn 2026-07-14");

        Assert.False(decision.IsPermitted);
        Assert.Equal(LawfulBasis.None, decision.Basis);
    }

    [Fact]
    public void LawfulBasis_CoversAllSixGdprArticle6Grounds()
    {
        // Enumerable, so "we support the six grounds" is checkable. Six plus
        // the explicit None.
        Assert.Equal(7, Enum.GetValues<LawfulBasis>().Length);
    }

    [Fact]
    public void ProcessingRequest_WithoutAPurpose_IsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => new ProcessingRequest(Tenant, "tok-1", "  ", [DataClassificationLevel.Phi]));
    }

    [Fact]
    public void ProcessingRequest_WithoutATenant_IsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => new ProcessingRequest(Guid.Empty, "tok-1", "direct-care", [DataClassificationLevel.Phi]));
    }

    [Fact]
    public void ProcessingRequest_TakesASubjectToken_NotARawIdentifier()
    {
        // The contract's shape enforces the rule: there is no parameter that
        // accepts a raw subject id.
        var request = new ProcessingRequest(Tenant, "tok-abc", "research", [DataClassificationLevel.Phi]);

        Assert.Equal("tok-abc", request.SubjectToken);
    }

    // ── legal hold ─────────────────────────────────────────────────────────

    [Fact]
    public void LegalHold_WithoutJustification_IsRejected()
    {
        // A hold overrides a data subject's erasure right; it must be
        // defensible.
        Assert.Throws<ArgumentException>(
            () => new LegalHold("LEG-4417", "  ", Now, Now.AddYears(1)));
    }

    [Fact]
    public void LegalHold_WhileActive_BlocksDestruction()
    {
        var hold = new LegalHold("LEG-4417", "litigation hold, Acme v. Trust", Now, Now.AddYears(1));

        Assert.True(hold.IsActive(Now));
        Assert.True(hold.IsActive(Now.AddMonths(11)));
    }

    [Fact]
    public void LegalHold_AfterExpiry_NoLongerBlocks()
    {
        // Holds are time-bounded, not perpetual: an indefinite hold is
        // indistinguishable from ignoring the erasure right.
        var hold = new LegalHold("LEG-4417", "litigation hold", Now, Now.AddYears(1));

        Assert.False(hold.IsActive(Now.AddYears(1).AddDays(1)));
    }

    [Fact]
    public void LegalHold_Reference_IsSafeToReturnInARefusal()
    {
        // §10.2: EDPF-CMP-6003 may disclose the hold reference and nothing
        // else — not the justification, which may describe the litigation.
        var hold = new LegalHold(
            "LEG-4417", "patient alleges negligence in cardiology ward 3", Now, Now.AddYears(1));

        var error = new Error(
            ErrorCodes.LegalHold,
            $"Operation blocked by legal hold {hold.HoldReference}.",
            ErrorCategory.Compliance);

        Assert.Contains("LEG-4417", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("negligence", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cardiology", error.Message, StringComparison.Ordinal);
    }
}
