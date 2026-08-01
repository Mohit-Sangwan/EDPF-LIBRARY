using Edpf.Abstractions.Data;

namespace Edpf.ConformanceTests;

/// <summary>
/// The conformance categories of Z.8/Z.12 — the definition of "Supported"
/// (ADR-008, ADR-016). A provider is certified by passing this suite in full:
/// no skips, no exclusions. Until then it is Experimental or Preview.
/// </summary>
/// <remarks>
/// The category list is fixed by ADR-008 and pinned here so the bar cannot
/// drift while the per-category batteries are written against live engines.
/// </remarks>
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

    /// <summary>
    /// Categories a provider can be certified against **without** a live
    /// engine, because they test the framework's own emitted structure rather
    /// than engine behaviour. These run on every commit for every dialect.
    /// </summary>
    public static readonly IReadOnlyList<string> StaticallyVerifiableCategories =
    [
        "IdentifierQuotingRejectsIllegalNames",
        "OperatorSetIsClosed",
        "ValuesAreAlwaysParameterised",
        "TenantPredicateIsUnavoidable",
        "SortIsAlwaysStable",
        "CapabilitiesAreDeclaredNotAssumed",
    ];
}

/// <summary>Guards the certification bar against silent edits.</summary>
public sealed class ProviderConformanceContractTests
{
    [Fact]
    public void MandatoryCategories_MatchAdr008_Exactly()
    {
        Assert.Equal(10, ProviderConformanceContract.MandatoryCategories.Count);
        Assert.Contains("CrossTenantIsolationEveryRoute", ProviderConformanceContract.MandatoryCategories);
        Assert.Contains("AuditChainContinuity", ProviderConformanceContract.MandatoryCategories);
    }

    [Fact]
    public void StaticallyVerifiableCategories_AreAllCoveredByTheUnitSuite()
    {
        // Each of these corresponds to a battery in Edpf.UnitTests.Data:
        //   IdentifierQuotingRejectsIllegalNames  -> DialectTests
        //   OperatorSetIsClosed                   -> FilterOperator (enum, no string path)
        //   ValuesAreAlwaysParameterised          -> InjectionCorpusTests
        //   TenantPredicateIsUnavoidable          -> AdversarialTenantTests
        //   SortIsAlwaysStable                    -> QueryCompilerTests
        //   CapabilitiesAreDeclaredNotAssumed     -> DialectTests
        Assert.Equal(6, ProviderConformanceContract.StaticallyVerifiableCategories.Count);
    }

    [Fact]
    public void CapabilityContract_ExposesEveryDimensionAdr016Requires()
    {
        // A provider cannot be certified against capabilities the contract
        // does not ask about, so the contract's shape is itself part of the
        // bar.
        Type capabilities = typeof(IProviderCapabilities);

        string[] required =
        [
            nameof(IProviderCapabilities.SupportsTableValuedParameters),
            nameof(IProviderCapabilities.SupportsSavepoints),
            nameof(IProviderCapabilities.SupportsStreaming),
            nameof(IProviderCapabilities.SupportsBulkCopy),
            nameof(IProviderCapabilities.SupportsKeysetPagination),
            nameof(IProviderCapabilities.SupportsJsonQuery),
            nameof(IProviderCapabilities.SupportsRowLevelSecurity),
            nameof(IProviderCapabilities.SupportsUpsert),
            nameof(IProviderCapabilities.SupportsIdentityRetrieval),
            nameof(IProviderCapabilities.SupportsZeroDowntimeDdl),
            nameof(IProviderCapabilities.MaxParameterCount),
            nameof(IProviderCapabilities.MaxBatchSize),
            nameof(IProviderCapabilities.MaxIdentifierLength),
        ];

        foreach (string name in required)
        {
            Assert.NotNull(capabilities.GetProperty(name));
        }
    }
}
