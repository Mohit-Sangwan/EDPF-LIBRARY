using System.Security.Cryptography;
using Edpf.Abstractions.Primitives;
using Edpf.Licensing;
using Edpf.Security;

namespace Edpf.UnitTests.Licensing;

/// <summary>
/// Phase 34b — offline entitlement validation and safe module gating.
/// </summary>
/// <remarks>
/// Signs with a real RSA key generated per test class, so verification is
/// exercised end to end rather than against a stub that always agrees. The
/// test project may touch <c>System.Security.Cryptography</c>; Z.10's
/// restriction applies to <c>src</c>, where the rule is about where crypto
/// lives, not about how it is tested.
/// </remarks>
public sealed class EntitlementTests : IDisposable
{
    private readonly RSA _issuerKey = RSA.Create(2048);
    private readonly RsaSignatureVerifier _verifier;
    private readonly EntitlementVerifier _entitlements;

    private static readonly DateTimeOffset Noon = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    public EntitlementTests()
    {
        _verifier = new RsaSignatureVerifier(_issuerKey.ExportSubjectPublicKeyInfo());
        _entitlements = new EntitlementVerifier(_verifier);
    }

    public void Dispose()
    {
        _verifier.Dispose();
        _issuerKey.Dispose();
    }

    private static Entitlement Licence(
        DateTimeOffset? issued = null, DateTimeOffset? expires = null, string deployment = "site-1")
        => new(
            deployment,
            ["healthcare", "analytics"],
            issued ?? Noon.AddDays(-30),
            expires ?? Noon.AddDays(60),
            "EDPF Licensing");

    private byte[] Sign(Entitlement entitlement)
        => _issuerKey.SignData(
            entitlement.CanonicalBytes(), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

    // ── signature ──────────────────────────────────────────────────────────

    [Fact]
    public void ValidlySignedEntitlement_IsAccepted()
    {
        Entitlement licence = Licence();

        EntitlementCheck check = _entitlements.Check(
            licence, Sign(licence), "site-1", Noon, DateTimeOffset.MinValue);

        Assert.True(check.IsValid);
    }

    [Fact]
    public void TamperedEntitlement_FailsSignatureVerification()
    {
        // The canonical form covers every field, so extending the expiry
        // invalidates the signature.
        Entitlement licence = Licence();
        byte[] signature = Sign(licence);

        Entitlement extended = new(
            licence.DeploymentId, licence.Modules, licence.IssuedUtc,
            licence.ExpiresUtc.AddYears(10), licence.Issuer);

        EntitlementCheck check = _entitlements.Check(
            extended, signature, "site-1", Noon, DateTimeOffset.MinValue);

        Assert.Equal(EntitlementStatus.SignatureInvalid, check.Status);
    }

    [Fact]
    public void EntitlementSignedByAnotherKey_IsRejected()
    {
        // A deployment that could mint its own would not need a licence.
        using RSA impostor = RSA.Create(2048);
        Entitlement licence = Licence();

        byte[] forged = impostor.SignData(
            licence.CanonicalBytes(), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        Assert.Equal(
            EntitlementStatus.SignatureInvalid,
            _entitlements.Check(licence, forged, "site-1", Noon, DateTimeOffset.MinValue).Status);
    }

    [Fact]
    public void ForgedEntitlement_ReportsOnlyTheSignatureFailure()
    {
        // Reporting "expired" for a forgery would tell an attacker which field
        // to edit next.
        Entitlement expired = Licence(expires: Noon.AddDays(-1));

        EntitlementCheck check = _entitlements.Check(
            expired, [1, 2, 3], "wrong-site", Noon, DateTimeOffset.MinValue);

        Assert.Equal(EntitlementStatus.SignatureInvalid, check.Status);
        Assert.Null(check.Entitlement);
        Assert.DoesNotContain("expired", check.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deployment", check.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptySignature_IsRejectedWithoutThrowing()
    {
        Entitlement licence = Licence();

        Assert.Equal(
            EntitlementStatus.SignatureInvalid,
            _entitlements.Check(licence, [], "site-1", Noon, DateTimeOffset.MinValue).Status);
    }

    [Fact]
    public void UndersizedSigningKey_IsRefusedAtConstruction()
    {
        // A deployment that quietly downgrades gets a system that still
        // verifies signatures and no longer means anything by it.
        using RSA weak = RSA.Create(1024);

        Assert.Throws<ArgumentException>(
            () => new RsaSignatureVerifier(weak.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void MalformedPublicKey_IsRefusedWithoutEchoingIt()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new RsaSignatureVerifier([0x00, 0x01, 0x02]));

        Assert.DoesNotContain("00", error.Message, StringComparison.Ordinal);
    }

    // ── canonical form ─────────────────────────────────────────────────────

    [Fact]
    public void ModuleOrder_DoesNotChangeTheSignedBytes()
    {
        // The same entitlement must sign identically whatever order the issuer
        // listed the modules in.
        var forward = new Entitlement("s", ["a", "b"], Noon, Noon.AddDays(1), "i");
        var reversed = new Entitlement("s", ["b", "a"], Noon, Noon.AddDays(1), "i");

        Assert.Equal(forward.CanonicalBytes(), reversed.CanonicalBytes());
    }

    [Fact]
    public void FieldBoundaries_CannotBeConfused()
    {
        // Length-prefixed, so a deployment id ending in a module name and a
        // module beginning with one cannot collide.
        var left = new Entitlement("ab", ["c"], Noon, Noon.AddDays(1), "i");
        var right = new Entitlement("a", ["bc"], Noon, Noon.AddDays(1), "i");

        Assert.NotEqual(left.CanonicalBytes(), right.CanonicalBytes());
    }

    [Fact]
    public void CanonicalForm_IsCultureIndependent()
    {
        // A signature that verified in London and failed in Istanbul would be
        // indistinguishable from tampering (Phase 27).
        Entitlement licence = Licence();
        byte[] first = licence.CanonicalBytes();

        System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal(first, licence.CanonicalBytes());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void EntitlementExpiringBeforeIssue_IsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => new Entitlement("s", ["a"], Noon, Noon.AddDays(-1), "i"));
    }

    // ── the offline problem: no revocation, and an untrusted clock ─────────

    [Fact]
    public void ClockRolledBackPastAPreviouslySeenTime_IsRefused()
    {
        // The offline attack. An air-gapped machine has no authority to check
        // its clock against, so winding it back would otherwise revive an
        // expired entitlement indefinitely.
        Entitlement licence = Licence();
        byte[] signature = Sign(licence);

        EntitlementCheck first = _entitlements.Check(
            licence, signature, "site-1", Noon, DateTimeOffset.MinValue);
        Assert.True(first.IsValid);

        EntitlementCheck second = _entitlements.Check(
            licence, signature, "site-1", Noon.AddDays(-10), first.HighWaterMark);

        Assert.Equal(EntitlementStatus.ClockRolledBack, second.Status);
    }

    [Fact]
    public void HighWaterMark_OnlyEverAdvances()
    {
        Entitlement licence = Licence();
        byte[] signature = Sign(licence);

        EntitlementCheck later = _entitlements.Check(
            licence, signature, "site-1", Noon.AddDays(10), DateTimeOffset.MinValue);
        EntitlementCheck earlier = _entitlements.Check(
            licence, signature, "site-1", Noon, later.HighWaterMark);

        Assert.Equal(later.HighWaterMark, earlier.HighWaterMark);
    }

    [Fact]
    public void ExpiredEntitlement_IsRefused()
    {
        // Expiry is the only revocation mechanism an offline deployment has,
        // which is why entitlements are short-lived.
        Entitlement licence = Licence(expires: Noon.AddDays(-1));

        Assert.Equal(
            EntitlementStatus.Expired,
            _entitlements.Check(licence, Sign(licence), "site-1", Noon, DateTimeOffset.MinValue).Status);
    }

    [Fact]
    public void EntitlementForAnotherDeployment_IsRefused()
    {
        Entitlement licence = Licence(deployment: "site-2");

        Assert.Equal(
            EntitlementStatus.WrongDeployment,
            _entitlements.Check(licence, Sign(licence), "site-1", Noon, DateTimeOffset.MinValue).Status);
    }

    [Fact]
    public void EntitlementNotYetInForce_IsRefused()
    {
        Entitlement licence = Licence(issued: Noon.AddDays(10), expires: Noon.AddDays(20));

        Assert.Equal(
            EntitlementStatus.NotYetValid,
            _entitlements.Check(licence, Sign(licence), "site-1", Noon, DateTimeOffset.MinValue).Status);
    }

    // ── a licence check is not allowed to be a safety hazard ──────────────

    [Fact]
    public void SafetyCriticalCapabilities_CannotBePlacedBehindAnEntitlement()
    {
        // The load-bearing decision of the phase, enforced structurally rather
        // than documented as guidance.
        var gate = new ModuleGate();

        foreach (string capability in ModuleGate.NeverGateable)
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() => gate.Register(capability));
            Assert.Contains("patient-safety hazard", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReadingAnExistingRecord_SurvivesAnExpiredLicence()
    {
        // A licence that lapsed over a bank holiday must not stop a clinician
        // opening a chart.
        var gate = new ModuleGate().Register("healthcare").Register("analytics");
        Entitlement expired = Licence(expires: Noon.AddDays(-1));

        Result applied = gate.Apply(_entitlements.Check(
            expired, Sign(expired), "site-1", Noon, DateTimeOffset.MinValue));

        Assert.True(applied.IsFailure);
        Assert.True(gate.IsAvailable("core.read"));
        Assert.True(gate.IsAvailable("core.audit.write"));
        Assert.True(gate.IsAvailable("core.breakglass"));
        Assert.True(gate.IsAvailable("core.export.subjectaccess"));
    }

    [Fact]
    public void SafetyCriticalCapabilities_SurviveHavingNoLicenceAtAll()
    {
        // The state a system is in while starting up, or after a licence file
        // has been deleted.
        var gate = new ModuleGate().Register("healthcare");

        Assert.True(gate.IsAvailable("core.read"));
        Assert.False(gate.IsAvailable("healthcare"));
    }

    [Fact]
    public void InvalidEntitlement_DisablesGateableModulesButDoesNotStopTheSystem()
    {
        var gate = new ModuleGate().Register("healthcare").Register("analytics");
        Entitlement expired = Licence(expires: Noon.AddDays(-1));

        gate.Apply(_entitlements.Check(
            expired, Sign(expired), "site-1", Noon, DateTimeOffset.MinValue));

        Assert.Empty(gate.EnabledModules);
        Assert.True(gate.IsAvailable("core.read"));
    }

    [Fact]
    public void ValidEntitlement_EnablesItsModules()
    {
        var gate = new ModuleGate().Register("healthcare").Register("analytics").Register("finance");
        Entitlement licence = Licence();

        Result applied = gate.Apply(_entitlements.Check(
            licence, Sign(licence), "site-1", Noon, DateTimeOffset.MinValue));

        Assert.True(applied.IsSuccess);
        Assert.True(gate.IsAvailable("healthcare"));
        Assert.True(gate.IsAvailable("analytics"));

        // Registered but not licensed — invisible, not error-producing.
        Assert.False(gate.IsAvailable("finance"));
    }

    [Fact]
    public void EntitlementNamingAnUnknownModule_DoesNotFail()
    {
        // Entitlements outlive releases. A licence issued for next year's
        // module list must not stop this year's binary starting.
        var gate = new ModuleGate().Register("healthcare");
        Entitlement licence = Licence();

        Assert.True(gate.Apply(_entitlements.Check(
            licence, Sign(licence), "site-1", Noon, DateTimeOffset.MinValue)).IsSuccess);
        Assert.True(gate.IsAvailable("healthcare"));
        Assert.False(gate.IsAvailable("analytics"));
    }

    [Fact]
    public void UnknownCapability_IsUnavailable_NotAssumedFree()
    {
        Assert.False(new ModuleGate().IsAvailable("something.nobody.registered"));
        Assert.False(new ModuleGate().IsAvailable("  "));
    }
}
