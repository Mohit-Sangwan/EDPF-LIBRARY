using System.Reflection;
using Edpf.Abstractions.Consistency;
using Edpf.Abstractions.Data;
using Edpf.Abstractions.Query;
using Edpf.Abstractions.Security;
using Edpf.Configuration;
using Edpf.Data.Query;
using Edpf.WalkingSkeleton.Api.Infrastructure.Audit;
using Edpf.WalkingSkeleton.Api.Pipeline;

namespace Edpf.ArchitectureTests;

/// <summary>
/// C4 §12.1 staleness control: the hand-authored code diagrams are asserted
/// against the compiled assemblies — documentation drift is a red build, not
/// a discovery months later.
/// </summary>
public sealed class DiagramConformanceTests
{
    /// <summary>§12.5: the envelope declares exactly the documented fields.</summary>
    [Fact]
    public void EncryptionEnvelope_MatchesDiagram125()
    {
        Type envelope = typeof(EncryptionEnvelope);

        Assert.NotNull(envelope.GetProperty("Version"));
        Assert.NotNull(envelope.GetProperty("AlgorithmId"));
        Assert.NotNull(envelope.GetProperty("KeyId"));
        Assert.NotNull(envelope.GetProperty("KeyVersion"));
        Assert.NotNull(envelope.GetProperty("Nonce"));
        Assert.NotNull(envelope.GetProperty("Ciphertext"));
        Assert.NotNull(envelope.GetProperty("Tag"));
        Assert.NotNull(envelope.GetMethod("Serialize"));
        Assert.NotNull(envelope.GetMethod("Deserialize"));
    }

    /// <summary>§12.3: the audit writer implements the documented chain shape.</summary>
    [Fact]
    public void AuditWriter_MatchesDiagram123()
    {
        Type writer = typeof(AuditWriter);

        Assert.NotNull(writer.GetMethod("WriteAsync"));
        Assert.NotNull(typeof(AuditChainVerifier).GetMethod("VerifyAsync"));
    }

    /// <summary>
    /// ADR-012: the canonical stage order is exactly the eleven stages of the
    /// approved decision, in the approved sequence.
    /// </summary>
    [Fact]
    public void PipelineStages_CanonicalOrder_MatchesAdr012()
    {
        string[] expected =
        [
            "Correlation", "TenantResolution", "Authentication", "Authorization",
            "Validation", "Idempotency", "Handler", "Transaction", "Audit",
            "Telemetry", "Response",
        ];

        Assert.Equal(expected, PipelineStages.CanonicalOrder);
    }

    /// <summary>
    /// ADR-013: configuration precedence is exactly the declared order,
    /// lowest priority first. Changing it requires superseding the ADR.
    /// </summary>
    [Fact]
    public void ConfigurationPrecedence_Order_MatchesAdr013()
    {
        ConfigurationSourceKind[] expected =
        [
            ConfigurationSourceKind.BuiltInDefaults,
            ConfigurationSourceKind.AppSettings,
            ConfigurationSourceKind.AppSettingsEnvironment,
            ConfigurationSourceKind.LegacyXml,
            ConfigurationSourceKind.UserSecrets,
            ConfigurationSourceKind.EnvironmentVariables,
            ConfigurationSourceKind.CommandLine,
            ConfigurationSourceKind.SecretStore,
        ];

        Assert.Equal(expected, EdpfConfigurationPrecedence.Order);
    }

    /// <summary>
    /// ADR-013: the secret store holds the highest precedence — it is the
    /// only source trusted with credentials.
    /// </summary>
    [Fact]
    public void ConfigurationPrecedence_SecretStore_IsHighestPriority()
    {
        Assert.Equal(ConfigurationSourceKind.SecretStore, EdpfConfigurationPrecedence.Order[^1]);
    }

    /// <summary>
    /// ADR-018: the filter-operator set is closed. If an operator could be
    /// supplied as a string anywhere, the framework's central injection
    /// defence would be optional rather than structural.
    /// </summary>
    [Fact]
    public void FilterOperators_AreAClosedEnum_NotStrings()
    {
        Assert.True(typeof(FilterOperator).IsEnum);

        // No public API in the query surface accepts an operator as text.
        IEnumerable<MethodInfo> operatorTakingMethods = typeof(Specification<>).Assembly
            .GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.Name.Contains("Where", StringComparison.Ordinal));

        foreach (MethodInfo method in operatorTakingMethods)
        {
            Assert.Contains(method.GetParameters(), p => p.ParameterType == typeof(FilterOperator));
        }
    }

    /// <summary>
    /// ADR-021: expand–migrate–contract is a closed, ordered set — a
    /// migration cannot declare a phase outside the discipline.
    /// </summary>
    [Fact]
    public void MigrationPhases_MatchAdr021()
    {
        Assert.Equal(
            [MigrationPhase.Expand, MigrationPhase.Migrate, MigrationPhase.Contract],
            Enum.GetValues<MigrationPhase>());
    }

    /// <summary>
    /// ADR-020: <see cref="ConcurrencyStrategy.Fail"/> is the default, so a
    /// caller that specifies nothing is told about conflicts rather than
    /// silently losing an update.
    /// </summary>
    [Fact]
    public void ConcurrencyStrategy_DefaultsToFail()
    {
        Assert.Equal(ConcurrencyStrategy.Fail, default(ConcurrencyStrategy));
    }

    /// <summary>
    /// Phase 09 §④: compensation failure is a distinct terminal status that
    /// demands escalation, not a variant of "failed".
    /// </summary>
    [Fact]
    public void SagaStatus_DistinguishesCompensationFailure()
    {
        var escalating = new SagaExecution(
            "T", SagaStatus.CompensationFailed, [], "step", null);
        var compensated = new SagaExecution(
            "T", SagaStatus.Compensated, [], "step", null);

        Assert.True(escalating.RequiresEscalation);
        Assert.False(compensated.RequiresEscalation);
    }
}
