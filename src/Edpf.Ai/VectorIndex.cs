using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;

namespace Edpf.Ai;

/// <summary>Turns text into a vector.</summary>
/// <remarks>
/// A seam, not an implementation. Embedding models are vendor-hosted or
/// self-hosted; what the framework contributes is that **the text being
/// embedded is still classified data**, and an embedding call to an external
/// provider is a disclosure exactly like an inference call.
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>A stable name for the audit trail.</summary>
    string ProviderName { get; }

    /// <summary>The dimension of the vectors this provider returns.</summary>
    int Dimensions { get; }

    /// <summary>True when the text leaves the deployment's trust boundary.</summary>
    bool IsExternal { get; }

    /// <summary>The highest classification that may be sent for embedding.</summary>
    DataClassificationLevel MaximumClassification { get; }

    /// <summary>
    /// Embeds text.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The vector, or a failure carrying no text.</returns>
    Task<Result<float[]>> EmbedAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// One indexed passage: its text, its vector, and what it is.
/// </summary>
/// <remarks>
/// **The classification travels with the chunk.** This is the property the
/// whole retrieval design rests on: a vector index over clinical records is
/// full of PHI, and a retrieval that forgot which passages were PHI would hand
/// them to whichever model the caller happened to configure.
/// </remarks>
public sealed class VectorChunk
{
    /// <summary>
    /// Records an indexed passage.
    /// </summary>
    /// <param name="chunkId">The passage's id.</param>
    /// <param name="tenantId">The owning tenant. Never empty.</param>
    /// <param name="text">The passage text.</param>
    /// <param name="vector">Its embedding.</param>
    /// <param name="classification">What the passage is.</param>
    /// <param name="sourceReference">Where it came from, for citation.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <exception cref="ArgumentException">The id is blank, the tenant is empty, or the vector is empty.</exception>
    public VectorChunk(
        string chunkId,
        Guid tenantId,
        string text,
        float[] vector,
        DataClassificationLevel classification,
        string sourceReference)
    {
        ChunkId = Guard.NotNullOrWhiteSpace(chunkId, nameof(chunkId));
        Text = Guard.NotNull(text, nameof(text));
        Vector = Guard.NotNull(vector, nameof(vector));
        SourceReference = Guard.NotNull(sourceReference, nameof(sourceReference));

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "An indexed chunk requires a tenant; an unscoped chunk is retrievable by everyone.",
                nameof(tenantId));
        }

        if (vector.Length == 0)
        {
            throw new ArgumentException("A chunk requires a non-empty vector.", nameof(vector));
        }

        TenantId = tenantId;
        Classification = classification;
    }

    /// <summary>The passage's id.</summary>
    public string ChunkId { get; }

    /// <summary>The owning tenant.</summary>
    public Guid TenantId { get; }

    /// <summary>The passage text.</summary>
    public string Text { get; }

#pragma warning disable CA1819 // The vector IS the data; copying it per access would dominate search cost.
    /// <summary>Its embedding. Treat as immutable.</summary>
    public float[] Vector { get; }
#pragma warning restore CA1819

    /// <summary>What the passage is.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>Where it came from, for citation.</summary>
    public string SourceReference { get; }
}

/// <summary>A chunk and how well it matched.</summary>
public sealed class VectorMatch
{
    /// <summary>Records a match.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="score">Cosine similarity, from -1 to 1.</param>
    public VectorMatch(VectorChunk chunk, double score)
    {
        Chunk = chunk;
        Score = score;
    }

    /// <summary>The chunk.</summary>
    public VectorChunk Chunk { get; }

    /// <summary>Cosine similarity, from -1 to 1. Higher is closer.</summary>
    public double Score { get; }
}

/// <summary>Stores and searches vectors.</summary>
public interface IVectorIndex
{
    /// <summary>Adds or replaces a chunk.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <returns>Success, or a tenant refusal.</returns>
    Result Upsert(VectorChunk chunk);

    /// <summary>Removes a chunk.</summary>
    /// <param name="chunkId">The chunk id.</param>
    /// <returns>Success, or not-found.</returns>
    Result Remove(string chunkId);

    /// <summary>
    /// Finds the closest chunks within the current tenant.
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="topK">How many to return.</param>
    /// <param name="minimumScore">Matches below this are not returned.</param>
    /// <returns>
    /// Matches within the current tenant only. There is no parameter that
    /// widens this.
    /// </returns>
    Result<IReadOnlyList<VectorMatch>> Search(float[] query, int topK, double minimumScore);
}

/// <summary>
/// An exhaustive in-memory vector index.
/// </summary>
/// <remarks>
/// <para>
/// Exact rather than approximate, and linear rather than graph-indexed. That
/// is the right trade at the scale this is for: a per-tenant clinical corpus of
/// thousands to low hundreds of thousands of passages, where an exhaustive scan
/// is milliseconds and an approximate index would trade recall for a speed
/// nobody needed. **A retrieval that silently misses the relevant passage is
/// worse than a slower one**, because the model then answers confidently from
/// what it did find.
/// </para>
/// <para>
/// A deployment past that scale swaps in pgvector, Qdrant or Azure AI Search
/// behind <see cref="IVectorIndex"/> — and inherits the tenant scoping and
/// classification carrying, because those live here rather than in the store.
/// </para>
/// </remarks>
public sealed class InMemoryVectorIndex : IVectorIndex
{
    private readonly Dictionary<string, VectorChunk> _chunks = new(StringComparer.Ordinal);
    private readonly ITenantContextAccessor _tenantAccessor;

    /// <summary>
    /// Composes the index.
    /// </summary>
    /// <param name="tenantAccessor">Ambient tenant.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tenantAccessor"/> is null.</exception>
    public InMemoryVectorIndex(ITenantContextAccessor tenantAccessor)
        => _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));

    /// <summary>How many chunks are indexed, across all tenants.</summary>
    public int Count => _chunks.Count;

    /// <inheritdoc />
    public Result Upsert(VectorChunk chunk)
    {
        Guard.NotNull(chunk, nameof(chunk));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId != chunk.TenantId)
        {
            return Result.Failure(NotFound());
        }

        _chunks[chunk.ChunkId] = chunk;
        return Result.Success();
    }

    /// <inheritdoc />
    public Result Remove(string chunkId)
    {
        Guard.NotNullOrWhiteSpace(chunkId, nameof(chunkId));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null
            || !_chunks.TryGetValue(chunkId, out VectorChunk? chunk)
            || chunk.TenantId != tenant.TenantId)
        {
            // Another tenant's chunk is indistinguishable from a missing one,
            // so the index is not an existence oracle over other tenants' ids.
            return Result.Failure(NotFound());
        }

        _chunks.Remove(chunkId);
        return Result.Success();
    }

    /// <inheritdoc />
    public Result<IReadOnlyList<VectorMatch>> Search(float[] query, int topK, double minimumScore)
    {
        Guard.NotNull(query, nameof(query));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Result.Failure<IReadOnlyList<VectorMatch>>(NotFound());
        }

        if (topK < 1)
        {
            return Result.Failure<IReadOnlyList<VectorMatch>>(new Error(
                ErrorCodes.ValidationFailed,
                "A search must return at least one result to be a search.",
                ErrorCategory.Validation));
        }

        var matches = new List<VectorMatch>();

        foreach (VectorChunk chunk in _chunks.Values)
        {
            // The tenant filter is applied before scoring, not after ranking.
            // Filtering afterwards would let another tenant's passages
            // displace this tenant's from the top-K and silently reduce recall.
            if (chunk.TenantId != tenant.TenantId || chunk.Vector.Length != query.Length)
            {
                continue;
            }

            double score = CosineSimilarity(query, chunk.Vector);
            if (score >= minimumScore)
            {
                matches.Add(new VectorMatch(chunk, score));
            }
        }

        matches.Sort((left, right) => right.Score.CompareTo(left.Score));

        if (matches.Count > topK)
        {
            matches.RemoveRange(topK, matches.Count - topK);
        }

        return matches;
    }

    /// <summary>
    /// Cosine similarity, computed without normalising the stored vectors in
    /// place.
    /// </summary>
    /// <param name="left">One vector.</param>
    /// <param name="right">The other.</param>
    /// <returns>Similarity from -1 to 1; zero when either vector has no magnitude.</returns>
    public static double CosineSimilarity(float[] left, float[] right)
    {
        Guard.NotNull(left, nameof(left));
        Guard.NotNull(right, nameof(right));

        if (left.Length != right.Length)
        {
            return 0;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;

        for (int i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftMagnitude += (double)left[i] * left[i];
            rightMagnitude += (double)right[i] * right[i];
        }

        // A zero vector has no direction, so it is not "perfectly similar" to
        // anything. Returning 1 here — which a naive 0/0 guard does — would
        // make an empty passage the top hit for every query.
        if (leftMagnitude == 0 || rightMagnitude == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private static Error NotFound() => new(
        ErrorCodes.NotFound,
        "The requested resource was not found.",
        ErrorCategory.NotFound);
}
