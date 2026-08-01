using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Consistency;

/// <summary>Where a saga has got to.</summary>
public enum SagaStatus
{
    /// <summary>Steps are executing.</summary>
    Running = 0,

    /// <summary>Every step completed.</summary>
    Completed = 1,

    /// <summary>A step failed; compensations are running in reverse order.</summary>
    Compensating = 2,

    /// <summary>Compensation completed; the saga is cleanly rolled back.</summary>
    Compensated = 3,

    /// <summary>
    /// **A compensation itself failed.** The case everyone forgets, and the
    /// one that causes silent financial and clinical drift in production
    /// (Phase 09 §④). The saga stops here and escalates to a human — it is
    /// never retried into oblivion or quietly marked failed.
    /// </summary>
    CompensationFailed = 4,

    /// <summary>The saga exceeded its deadline.</summary>
    TimedOut = 5,
}

/// <summary>
/// A long-running process with compensation (ADR-003, Phase 09 §④). Sagas
/// replace the cross-store transactions that are not available: each step
/// commits locally, and failure runs the completed steps' compensations in
/// reverse.
/// </summary>
/// <typeparam name="TState">The saga's persisted state.</typeparam>
public interface ISaga<TState>
    where TState : class
{
    /// <summary>Stable saga type name, used for storage and metrics.</summary>
    string SagaType { get; }

    /// <summary>The ordered steps.</summary>
    IReadOnlyList<ISagaStep<TState>> Steps { get; }

    /// <summary>How long the saga may run before it is declared timed out.</summary>
    TimeSpan Timeout { get; }
}

/// <summary>One step of a saga, with its compensation.</summary>
/// <typeparam name="TState">The saga's state.</typeparam>
public interface ISagaStep<TState>
    where TState : class
{
    /// <summary>Stable step name, used in state and diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Executes the step.
    /// </summary>
    /// <param name="state">The saga state; the step may mutate it.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    /// <returns>Success, or the failure that triggers compensation.</returns>
    Task<Result> ExecuteAsync(TState state, CancellationToken cancellationToken);

    /// <summary>
    /// Undoes this step's effect.
    /// </summary>
    /// <param name="state">The saga state.</param>
    /// <param name="cancellationToken">Cancels the compensation.</param>
    /// <returns>
    /// Success, or a failure that escalates to
    /// <see cref="SagaStatus.CompensationFailed"/>. Compensations must be
    /// idempotent: they run after at-least-once delivery and after retries.
    /// </returns>
    Task<Result> CompensateAsync(TState state, CancellationToken cancellationToken);
}

/// <summary>Drives sagas and persists their progress.</summary>
public interface ISagaCoordinator
{
    /// <summary>
    /// Runs a saga to completion, or compensates it.
    /// </summary>
    /// <typeparam name="TState">The saga's state type.</typeparam>
    /// <param name="saga">The saga definition.</param>
    /// <param name="state">Initial state.</param>
    /// <param name="cancellationToken">Cancels between steps.</param>
    /// <returns>The terminal outcome, including which step failed and why.</returns>
    Task<Result<SagaExecution>> RunAsync<TState>(
        ISaga<TState> saga,
        TState state,
        CancellationToken cancellationToken)
        where TState : class;
}

/// <summary>The outcome of a saga run.</summary>
public sealed class SagaExecution
{
    /// <summary>
    /// Initializes an outcome.
    /// </summary>
    /// <param name="sagaType">The saga type.</param>
    /// <param name="status">Terminal status.</param>
    /// <param name="completedSteps">Steps that executed successfully, in order.</param>
    /// <param name="failedStep">The step that failed, if any.</param>
    /// <param name="failure">The failure that stopped the saga, if any.</param>
    public SagaExecution(
        string sagaType,
        SagaStatus status,
        IReadOnlyList<string> completedSteps,
        string? failedStep,
        Error? failure)
    {
        SagaType = sagaType ?? throw new ArgumentNullException(nameof(sagaType));
        Status = status;
        CompletedSteps = completedSteps ?? throw new ArgumentNullException(nameof(completedSteps));
        FailedStep = failedStep;
        Failure = failure;
    }

    /// <summary>The saga type.</summary>
    public string SagaType { get; }

    /// <summary>Terminal status.</summary>
    public SagaStatus Status { get; }

    /// <summary>Steps that executed successfully, in order.</summary>
    public IReadOnlyList<string> CompletedSteps { get; }

    /// <summary>The step that failed, if any.</summary>
    public string? FailedStep { get; }

    /// <summary>The failure that stopped the saga, if any.</summary>
    public Error? Failure { get; }

    /// <summary>
    /// True when a human must intervene: compensation failed, so the system
    /// is in a state no automated path can resolve.
    /// </summary>
    public bool RequiresEscalation => Status == SagaStatus.CompensationFailed;
}
