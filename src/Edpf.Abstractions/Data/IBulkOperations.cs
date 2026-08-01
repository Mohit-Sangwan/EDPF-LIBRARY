using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Data;

/// <summary>What happens when part of a bulk operation fails (Phase 13).</summary>
public enum BulkFailurePolicy
{
    /// <summary>
    /// The whole batch commits or none of it does. The safe default for
    /// clinical and financial ingest.
    /// </summary>
    AllOrNothing = 0,

    /// <summary>
    /// Successful rows commit; failures are reported per row. Chosen
    /// explicitly, per call, for tolerant ingest — never inferred.
    /// </summary>
    ContinueAndReport = 1,
}

/// <summary>
/// High-throughput insert (Phase 13).
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <remarks>
/// <para>
/// **The critical rule:** a bulk path must not bypass tenant scoping, audit,
/// or classification-driven encryption. A bulk insert that skips field-level
/// PHI encryption because it uses a native fast path is a breach waiting to
/// happen — and it is an easy mistake, because the native paths
/// (<c>SqlBulkCopy</c>, <c>COPY</c>, array binding) all work at a layer below
/// the one where those concerns normally live.
/// </para>
/// <para>
/// The conformance suite asserts parity with the single-row paths rather than
/// trusting the implementation.
/// </para>
/// </remarks>
public interface IBulkInserter<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Inserts a stream of entities.
    /// </summary>
    /// <param name="entities">
    /// The source. Enumerated lazily so ingest never requires the full set in
    /// memory — a 50-million-row import must run in flat memory.
    /// </param>
    /// <param name="options">Batch size, failure policy and progress reporting.</param>
    /// <param name="cancellationToken">Cancels between batches; a batch in flight completes or rolls back.</param>
    /// <returns>What was written, and what failed.</returns>
    Task<Result<BulkResult>> InsertAsync(
        IEnumerable<TEntity> entities,
        BulkOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Bulk operation settings.</summary>
public sealed class BulkOptions
{
    /// <summary>
    /// Initializes options.
    /// </summary>
    /// <param name="batchSize">
    /// Rows per round trip. Capped by the provider's
    /// <see cref="IProviderCapabilities.MaxBatchSize"/> and parameter limit.
    /// </param>
    /// <param name="failurePolicy">What to do on partial failure. Explicit by design.</param>
    /// <param name="progress">Optional progress reporting for long ingests.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is not positive.</exception>
    public BulkOptions(int batchSize, BulkFailurePolicy failurePolicy, IProgress<BulkProgress>? progress = null)
    {
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");
        }

        BatchSize = batchSize;
        FailurePolicy = failurePolicy;
        Progress = progress;
    }

    /// <summary>Rows per round trip.</summary>
    public int BatchSize { get; }

    /// <summary>What to do on partial failure.</summary>
    public BulkFailurePolicy FailurePolicy { get; }

    /// <summary>Progress reporting, if requested.</summary>
    public IProgress<BulkProgress>? Progress { get; }
}

/// <summary>
/// Progress through a bulk operation.
/// </summary>
/// <remarks>
/// A plain readonly struct rather than a <c>record struct</c>: positional
/// records need <c>IsExternalInit</c>, which does not exist on Tier 3 TFMs,
/// and <c>Edpf.Abstractions</c> may not reference a polyfill (EDPF0001) or
/// use <c>#if</c> (EDPF0002). ADR-002's cost, paid explicitly.
/// </remarks>
public readonly struct BulkProgress : IEquatable<BulkProgress>
{
    /// <summary>Initializes progress.</summary>
    /// <param name="rowsProcessed">Rows processed so far.</param>
    /// <param name="rowsFailed">Rows that failed so far.</param>
    public BulkProgress(long rowsProcessed, long rowsFailed)
    {
        RowsProcessed = rowsProcessed;
        RowsFailed = rowsFailed;
    }

    /// <summary>Rows processed so far.</summary>
    public long RowsProcessed { get; }

    /// <summary>Rows that failed so far.</summary>
    public long RowsFailed { get; }

    /// <inheritdoc />
    public bool Equals(BulkProgress other)
        => RowsProcessed == other.RowsProcessed && RowsFailed == other.RowsFailed;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BulkProgress other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            return (RowsProcessed.GetHashCode() * 397) ^ RowsFailed.GetHashCode();
        }
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(BulkProgress left, BulkProgress right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(BulkProgress left, BulkProgress right) => !left.Equals(right);
}

/// <summary>What a bulk operation did.</summary>
public sealed class BulkResult
{
    /// <summary>
    /// Initializes a result.
    /// </summary>
    /// <param name="rowsWritten">Rows durably written.</param>
    /// <param name="failures">Per-row failures, when the policy allowed continuing.</param>
    /// <param name="duration">How long the operation took.</param>
    public BulkResult(long rowsWritten, IReadOnlyList<BulkRowFailure> failures, TimeSpan duration)
    {
        RowsWritten = rowsWritten;
        Failures = failures ?? throw new ArgumentNullException(nameof(failures));
        Duration = duration;
    }

    /// <summary>Rows durably written.</summary>
    public long RowsWritten { get; }

    /// <summary>Per-row failures.</summary>
    public IReadOnlyList<BulkRowFailure> Failures { get; }

    /// <summary>How long the operation took.</summary>
    public TimeSpan Duration { get; }
}

/// <summary>One row that failed during a tolerant bulk operation.</summary>
public readonly struct BulkRowFailure : IEquatable<BulkRowFailure>
{
    /// <summary>Initializes a row failure.</summary>
    /// <param name="rowIndex">Zero-based position in the source.</param>
    /// <param name="error">Why it failed. Carries no row data — the row may be PHI.</param>
    public BulkRowFailure(long rowIndex, Error error)
    {
        RowIndex = rowIndex;
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    /// <summary>Zero-based position in the source.</summary>
    public long RowIndex { get; }

    /// <summary>
    /// Why it failed. Deliberately carries no row content: a failure report
    /// from a PHI import must not become a PHI export.
    /// </summary>
    public Error Error { get; }

    /// <inheritdoc />
    public bool Equals(BulkRowFailure other)
        => RowIndex == other.RowIndex && Equals(Error, other.Error);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BulkRowFailure other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            return (RowIndex.GetHashCode() * 397) ^ (Error?.GetHashCode() ?? 0);
        }
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(BulkRowFailure left, BulkRowFailure right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(BulkRowFailure left, BulkRowFailure right) => !left.Equals(right);
}
