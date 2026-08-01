using Edpf.Abstractions.Identity;

namespace Edpf.UnitTests.Identity;

/// <summary>
/// Phase 21's hierarchical scopes — organization → facility → department →
/// unit → resource. The original specification listed "Department Security",
/// "Hospital Security" and "Facility Security" as separate features; they are
/// one model at different depths.
/// </summary>
public sealed class AuthorizationScopeTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void Contains_ScopeBelowIt_IsTrue()
    {
        AuthorizationScope facility = AuthorizationScope.Create(TenantA, "org", "st-marys");
        AuthorizationScope ward = AuthorizationScope.Create(TenantA, "org", "st-marys", "cardiology", "ward-3");

        Assert.True(facility.Contains(ward));
    }

    [Fact]
    public void Contains_ScopeAboveIt_IsFalse()
    {
        // Authority does not flow upward: a ward lead does not run the hospital.
        AuthorizationScope facility = AuthorizationScope.Create(TenantA, "org", "st-marys");
        AuthorizationScope ward = AuthorizationScope.Create(TenantA, "org", "st-marys", "cardiology", "ward-3");

        Assert.False(ward.Contains(facility));
    }

    [Fact]
    public void Contains_SiblingScope_IsFalse()
    {
        AuthorizationScope cardiology = AuthorizationScope.Create(TenantA, "org", "st-marys", "cardiology");
        AuthorizationScope oncology = AuthorizationScope.Create(TenantA, "org", "st-marys", "oncology");

        Assert.False(cardiology.Contains(oncology));
    }

    [Fact]
    public void Contains_PrefixSharingButDistinctSegment_IsFalse()
    {
        // The subtle one. A substring check would make "cardio" contain
        // "cardiology" and silently widen every grant whose name happens to
        // be a prefix of another's.
        AuthorizationScope cardio = AuthorizationScope.Create(TenantA, "org", "st-marys", "cardio");
        AuthorizationScope cardiology = AuthorizationScope.Create(TenantA, "org", "st-marys", "cardiology");

        Assert.False(cardio.Contains(cardiology));
        Assert.False(cardiology.Contains(cardio));
    }

    [Fact]
    public void Contains_SameScope_IsTrue()
    {
        AuthorizationScope scope = AuthorizationScope.Create(TenantA, "org", "st-marys");

        Assert.True(scope.Contains(AuthorizationScope.Create(TenantA, "org", "st-marys")));
    }

    [Fact]
    public void Contains_OtherTenantsScope_IsFalseEvenWhenPathsMatch()
    {
        // Scopes never span tenants, however identical the org chart.
        AuthorizationScope a = AuthorizationScope.Create(TenantA, "org", "st-marys");
        AuthorizationScope b = AuthorizationScope.Create(TenantB, "org", "st-marys", "cardiology");

        Assert.False(a.Contains(b));
    }

    [Fact]
    public void Level_ReflectsDepth()
    {
        Assert.Equal(ScopeLevel.Organization, AuthorizationScope.Create(TenantA, "org").Level);
        Assert.Equal(ScopeLevel.Facility, AuthorizationScope.Create(TenantA, "org", "f").Level);
        Assert.Equal(ScopeLevel.Department, AuthorizationScope.Create(TenantA, "org", "f", "d").Level);
        Assert.Equal(ScopeLevel.Unit, AuthorizationScope.Create(TenantA, "org", "f", "d", "u").Level);
    }

    [Fact]
    public void Parent_Always_ReturnsOneLevelUp()
    {
        AuthorizationScope ward = AuthorizationScope.Create(TenantA, "org", "st-marys", "cardiology", "ward-3");

        AuthorizationScope? department = ward.Parent();

        Assert.Equal(ScopeLevel.Department, department!.Level);
        Assert.True(department.Contains(ward));
    }

    [Fact]
    public void Parent_AtOrganizationLevel_IsNull()
    {
        Assert.Null(AuthorizationScope.Create(TenantA, "org").Parent());
    }

    [Fact]
    public void Create_WithoutTenant_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => AuthorizationScope.Create(Guid.Empty, "org"));
    }

    [Theory]
    [InlineData("org/../other")]
    [InlineData("org*")]
    [InlineData("org?")]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_SegmentThatCouldWidenTheGrant_IsRejected(string segment)
    {
        // A separator or wildcard would let one grant match scopes it was
        // never given.
        Assert.Throws<ArgumentException>(() => AuthorizationScope.Create(TenantA, segment));
    }

    [Fact]
    public void Create_TooManySegments_IsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => AuthorizationScope.Create(TenantA, "a", "b", "c", "d", "e", "f"));
    }

    [Fact]
    public void Create_NoSegments_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => AuthorizationScope.Create(TenantA));
    }
}

/// <summary>
/// Phase 21: "Every authorization decision, including denials, must be
/// audited — verified by test."
/// </summary>
public sealed class AuthorizationDecisionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void Deny_Always_CarriesAReasonForTheAuditTrail()
    {
        AuthorizationScope scope = AuthorizationScope.Create(Tenant, "org", "st-marys");

        AuthorizationDecision decision = AuthorizationDecision.Deny(
            "patients:read", scope, "grant covers oncology only");

        Assert.False(decision.IsAllowed);
        Assert.Equal("patients:read", decision.RequiredPermission);
        Assert.Equal("grant covers oncology only", decision.Reason);
    }

    [Fact]
    public void Allow_Always_CarriesAReasonToo()
    {
        // A denial is the record that proves a control worked; an allow is
        // the record that answers "who could see this?" during an incident.
        AuthorizationDecision decision = AuthorizationDecision.Allow(
            "patients:read", AuthorizationScope.Create(Tenant, "org"), "org-level clinician grant");

        Assert.True(decision.IsAllowed);
        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void Decision_Reason_IsSeparateFromTheCallerFacingPermission()
    {
        // The reason goes to the audit trail; the caller learns only which
        // permission was required (§10.2 EDPF-AUTHZ-2101).
        AuthorizationDecision decision = AuthorizationDecision.Deny(
            "patients:read", null, "user 4471 lacks grant on facility st-marys");

        Assert.Equal("patients:read", decision.RequiredPermission);
        Assert.DoesNotContain("4471", decision.RequiredPermission, StringComparison.Ordinal);
    }
}
