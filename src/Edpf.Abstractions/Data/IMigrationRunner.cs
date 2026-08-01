using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Data;

/// <summary>
/// Applies schema migrations (Phase 11 §④). Zero downtime is the default
/// expectation; a migration that needs downtime must declare it and be
/// approved (ADR-021).
/// </summary>
public interface IMigrationRunner
{
    /// <summary>
    /// Applies every pending migration.
    /// </summary>
    /// <param name="cancellationToken">Cancels before the next migration; never mid-migration.</param>
    /// <returns>What was applied, or the failure that stopped the run.</returns>
    /// <remarks>
    /// Implementations take a distributed lock first. Ten pods starting
    /// simultaneously in a rolling deployment must produce exactly one
    /// migration execution — this is a common and destructive Kubernetes
    /// failure mode, not a theoretical one.
    /// </remarks>
    Task<Result<MigrationRunReport>> MigrateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reports what would be applied, with a duration estimate and lock
    /// analysis, without changing anything.
    /// </summary>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <returns>The pre-flight report.</returns>
    Task<Result<MigrationPlan>> PlanAsync(CancellationToken cancellationToken);
}

/// <summary>One migration.</summary>
public interface IMigration
{
    /// <summary>Monotonic version. Migrations apply in ascending order.</summary>
    long Version { get; }

    /// <summary>Human-readable name, used in logs and the version store.</summary>
    string Name { get; }

    /// <summary>
    /// Which phase of expand–migrate–contract this migration is (ADR-021).
    /// </summary>
    MigrationPhase Phase { get; }

    /// <summary>
    /// True when this migration cannot be reversed. Declaring it forces the
    /// author to think about it, and forces the reviewer to see it.
    /// </summary>
    bool IsIrreversible { get; }

    /// <summary>
    /// True when this migration takes a lock that blocks readers or writers.
    /// A true value requires explicit approval — it is a planned outage.
    /// </summary>
    bool RequiresDowntime { get; }
}

/// <summary>
/// The expand–migrate–contract phases (ADR-021). Mandatory for every breaking
/// change: expand adds the new shape alongside the old, migrate moves data
/// while both are live, contract removes the old shape at least one release
/// later — so a rollback never lands on a schema that cannot serve the
/// previous version.
/// </summary>
public enum MigrationPhase
{
    /// <summary>Additive only: new nullable columns, new tables, new indexes. Always safe.</summary>
    Expand = 0,

    /// <summary>Moves or backfills data. Safe while both shapes exist.</summary>
    Migrate = 1,

    /// <summary>
    /// Removes the old shape. **Breaking** — permitted only once every
    /// deployed version reads the new shape.
    /// </summary>
    Contract = 2,
}

/// <summary>What a migration run did.</summary>
public sealed class MigrationRunReport
{
    /// <summary>
    /// Initializes a report.
    /// </summary>
    /// <param name="applied">Versions applied, in order.</param>
    /// <param name="skippedBecauseLockHeld">
    /// True when another instance held the migration lock and this one
    /// correctly did nothing — the expected outcome for all but one pod in a
    /// rolling deployment.
    /// </param>
    /// <param name="duration">How long the run took.</param>
    public MigrationRunReport(IReadOnlyList<long> applied, bool skippedBecauseLockHeld, TimeSpan duration)
    {
        Applied = applied ?? throw new ArgumentNullException(nameof(applied));
        SkippedBecauseLockHeld = skippedBecauseLockHeld;
        Duration = duration;
    }

    /// <summary>Versions applied, in order.</summary>
    public IReadOnlyList<long> Applied { get; }

    /// <summary>True when another instance held the lock and this one stood down.</summary>
    public bool SkippedBecauseLockHeld { get; }

    /// <summary>How long the run took.</summary>
    public TimeSpan Duration { get; }
}

/// <summary>A pre-flight migration plan.</summary>
public sealed class MigrationPlan
{
    /// <summary>
    /// Initializes a plan.
    /// </summary>
    /// <param name="pending">Migrations that would be applied, in order.</param>
    /// <param name="blockingWarnings">
    /// Operations that will take a blocking lock on a large table — the
    /// warning an operator needs before, not after.
    /// </param>
    public MigrationPlan(IReadOnlyList<IMigration> pending, IReadOnlyList<string> blockingWarnings)
    {
        Pending = pending ?? throw new ArgumentNullException(nameof(pending));
        BlockingWarnings = blockingWarnings ?? throw new ArgumentNullException(nameof(blockingWarnings));
    }

    /// <summary>Migrations that would be applied, in order.</summary>
    public IReadOnlyList<IMigration> Pending { get; }

    /// <summary>Operations that will block readers or writers.</summary>
    public IReadOnlyList<string> BlockingWarnings { get; }

    /// <summary>True when the plan contains a migration that needs approval.</summary>
    public bool RequiresApproval => BlockingWarnings.Count > 0;
}

/// <summary>
/// Compares the live schema against what the applied migrations imply
/// (Phase 11 §④).
/// </summary>
public interface ISchemaDriftDetector
{
    /// <summary>
    /// Detects drift.
    /// </summary>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>
    /// The differences found; empty means the schema matches. Drift fails
    /// **readiness, not liveness**, so a drifted instance is removed from load
    /// balancing rather than crash-looping.
    /// </returns>
    Task<Result<IReadOnlyList<string>>> DetectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The distributed lock that makes concurrent startup safe (Phase 11 §④).
/// </summary>
public interface IMigrationLock
{
    /// <summary>
    /// Attempts to acquire the migration lock.
    /// </summary>
    /// <param name="holderId">Identifies the acquiring instance, for diagnostics.</param>
    /// <param name="leaseDuration">
    /// How long the lease survives without renewal, so a crashed migrator
    /// does not block the estate forever.
    /// </param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>
    /// A lease when acquired; null when another instance holds it. A null is
    /// the normal, correct outcome for every pod but one.
    /// </returns>
    Task<IMigrationLease?> TryAcquireAsync(
        string holderId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
}

/// <summary>
/// A held migration lock.
/// </summary>
/// <remarks>
/// Release is an explicit async call rather than <c>IAsyncDisposable</c>:
/// that interface does not exist on Tier 3 TFMs (ADR-002), and
/// <c>Edpf.Abstractions</c> carries zero package references (EDPF0001) so it
/// cannot polyfill. Releasing a distributed lock is I/O and deserves to be
/// visible at the call site regardless.
/// </remarks>
public interface IMigrationLease
{
    /// <summary>The instance holding the lease.</summary>
    string HolderId { get; }

    /// <summary>When the lease expires if it is not renewed.</summary>
    DateTimeOffset ExpiresUtc { get; }

    /// <summary>
    /// Extends the lease. Long migrations renew periodically so a slow run is
    /// not mistaken for a crashed one.
    /// </summary>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <returns>Success, or failure when the lease was already lost.</returns>
    Task<Result> RenewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Releases the lease so the next instance may migrate.
    /// </summary>
    /// <param name="cancellationToken">Cancels the release; the lease still expires on its own.</param>
    /// <returns>Success once released.</returns>
    Task<Result> ReleaseAsync(CancellationToken cancellationToken);
}
