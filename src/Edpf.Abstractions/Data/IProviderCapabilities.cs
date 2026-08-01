namespace Edpf.Abstractions.Data;

/// <summary>
/// What a provider can actually do (ADR-016). Providers differ
/// irreconcilably — Oracle has no <c>IDENTITY</c> in the SQL Server sense,
/// MongoDB has no joins, Cosmos constrains partition keys — so EDPF does not
/// pretend they are the same. Instead each provider declares its capabilities
/// honestly, and callers either degrade explicitly or receive
/// <see cref="Primitives.ErrorCodes.CapabilityNotSupported"/>.
/// </summary>
/// <remarks>
/// **A claimed capability is a tested capability** (Z.12). The conformance
/// suite verifies every <c>true</c> here against real behaviour; a provider
/// that over-claims fails certification. The framework never emits
/// silently-wrong SQL to paper over a missing feature.
/// </remarks>
public interface IProviderCapabilities
{
    /// <summary>Table-valued parameters (SQL Server) or array parameters (PostgreSQL).</summary>
    bool SupportsTableValuedParameters { get; }

    /// <summary>Savepoints within a transaction. False means partial rollback is unavailable (ADR-003).</summary>
    bool SupportsSavepoints { get; }

    /// <summary>Server-side streaming with bounded client memory.</summary>
    bool SupportsStreaming { get; }

    /// <summary>A native bulk-copy path (<c>SqlBulkCopy</c>, <c>COPY</c>, array binding).</summary>
    bool SupportsBulkCopy { get; }

    /// <summary>Keyset (cursor) pagination — required for correctness past a few hundred thousand rows.</summary>
    bool SupportsKeysetPagination { get; }

    /// <summary>Querying inside JSON documents.</summary>
    bool SupportsJsonQuery { get; }

    /// <summary>Native row-level security.</summary>
    bool SupportsRowLevelSecurity { get; }

    /// <summary>An atomic upsert/merge statement.</summary>
    bool SupportsUpsert { get; }

    /// <summary>Reading generated identity values in the same round trip.</summary>
    bool SupportsIdentityRetrieval { get; }

    /// <summary>Schema changes without blocking readers and writers.</summary>
    bool SupportsZeroDowntimeDdl { get; }

    /// <summary>Maximum parameters in one command. Batching must respect this or the driver fails at runtime.</summary>
    int MaxParameterCount { get; }

    /// <summary>Maximum statements EDPF will pack into one round trip.</summary>
    int MaxBatchSize { get; }

    /// <summary>Maximum identifier length; migrations must not generate longer names.</summary>
    int MaxIdentifierLength { get; }
}
