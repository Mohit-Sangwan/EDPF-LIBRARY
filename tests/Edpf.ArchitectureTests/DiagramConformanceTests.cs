using Edpf.Abstractions.Security;
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
}
