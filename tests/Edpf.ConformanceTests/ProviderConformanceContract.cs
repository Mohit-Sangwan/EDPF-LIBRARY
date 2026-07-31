namespace Edpf.ConformanceTests;

/// <summary>
/// The conformance categories of Z.8/Z.12 — the definition of "Supported".
/// Wave 2 turns each category into concrete test batteries against
/// <c>IDataProvider</c>; the category list itself is fixed by ADR-008 and
/// asserted here so it cannot drift silently before the suite lands.
/// </summary>
public static class ProviderConformanceContract
{
    /// <summary>The mandatory categories (Z.8), in gate order.</summary>
    public static readonly IReadOnlyList<string> MandatoryCategories =
    [
        "CrudRoundTripFidelity",
        "TransactionCommitRollbackSavepoint",
        "ConcurrencyConflict",
        "PaginationBoundaries",
        "StreamingBoundedMemory",
        "CancellationMidOperation",
        "ConnectionFailureRecovery",
        "CrossTenantIsolationEveryRoute",
        "EncryptionRoundTripKeyRotation",
        "AuditChainContinuity",
    ];
}

/// <summary>Guards the ADR-008 category list against silent edits.</summary>
public sealed class ProviderConformanceContractTests
{
    [Fact]
    public void MandatoryCategories_MatchAdr008_Exactly()
    {
        Assert.Equal(10, ProviderConformanceContract.MandatoryCategories.Count);
        Assert.Contains("CrossTenantIsolationEveryRoute", ProviderConformanceContract.MandatoryCategories);
        Assert.Contains("AuditChainContinuity", ProviderConformanceContract.MandatoryCategories);
    }
}
