using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Consistency;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;
using Microsoft.Extensions.Logging;

namespace Edpf.Data.Consistency;

/// <summary>
/// Runs sagas with reverse-order compensation and explicit escalation when a
/// compensation itself fails (Phase 09 §④).
/// </summary>
/// <remarks>
/// The compensation-failure path is the reason this class exists rather than
/// a <c>foreach</c> at a call site. When a step's compensation fails, the
/// system is in a state no automated path can resolve: some effects are
/// applied and cannot be withdrawn. Retrying forever hides it; marking the
/// saga "failed" and moving on causes silent financial and clinical drift.
/// The only correct response is to stop, record precisely what is applied,
/// and page a human.
/// </remarks>
public sealed class SagaCoordinator(ILogger<SagaCoordinator> logger) : ISagaCoordinator
{
    private readonly ILogger _logger = Guard.NotNull(logger, nameof(logger));

    /// <inheritdoc />
    public async Task<Result<SagaExecution>> RunAsync<TState>(
        ISaga<TState> saga,
        TState state,
        CancellationToken cancellationToken)
        where TState : class
    {
        Guard.NotNull(saga, nameof(saga));
        Guard.NotNull(state, nameof(state));

        var completed = new List<string>();

        for (int i = 0; i < saga.Steps.Count; i++)
        {
            ISagaStep<TState> step = saga.Steps[i];

            Result executed;
            try
            {
                executed = await step.ExecuteAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A throwing step is a failing step: it compensates like any
                // other, rather than escaping and leaving the saga dangling.
                SagaLog.StepThrew(_logger, saga.SagaType, step.Name, ex);
                executed = Result.Failure(new Error(
                    ErrorCodes.TransactionFailed,
                    $"Saga step '{step.Name}' threw.",
                    ErrorCategory.Internal));
            }

            if (executed.IsSuccess)
            {
                completed.Add(step.Name);
                continue;
            }

            SagaLog.StepFailed(_logger, saga.SagaType, step.Name, executed.Error!.Code);
            return await CompensateAsync(saga, state, completed, step.Name, executed.Error!, cancellationToken)
                .ConfigureAwait(false);
        }

        SagaLog.Completed(_logger, saga.SagaType, completed.Count);
        return Result.Success(new SagaExecution(
            saga.SagaType, SagaStatus.Completed, completed, failedStep: null, failure: null));
    }

    private async Task<Result<SagaExecution>> CompensateAsync<TState>(
        ISaga<TState> saga,
        TState state,
        List<string> completed,
        string failedStep,
        Error failure,
        CancellationToken cancellationToken)
        where TState : class
    {
        // Reverse order: the most recent effect is undone first.
        for (int i = completed.Count - 1; i >= 0; i--)
        {
            ISagaStep<TState> step = FindStep(saga, completed[i]);

            Result compensated;
            try
            {
                // Compensation runs to completion even under cancellation:
                // abandoning it half-done is precisely the state this exists
                // to prevent.
                compensated = await step.CompensateAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SagaLog.CompensationThrew(_logger, saga.SagaType, step.Name, ex);
                compensated = Result.Failure(new Error(
                    ErrorCodes.TransactionFailed,
                    $"Compensation for '{step.Name}' threw.",
                    ErrorCategory.Internal));
            }

            if (compensated.IsFailure)
            {
                // Stop immediately. Continuing would apply further
                // compensations on top of an inconsistent state, making the
                // eventual manual repair harder rather than easier.
                List<string> stillApplied = completed.GetRange(0, i + 1);
                SagaLog.CompensationFailed(
                    _logger, saga.SagaType, step.Name, stillApplied.Count, compensated.Error!.Code);

                return Result.Success(new SagaExecution(
                    saga.SagaType,
                    SagaStatus.CompensationFailed,
                    stillApplied,
                    step.Name,
                    compensated.Error));
            }
        }

        SagaLog.Compensated(_logger, saga.SagaType, failedStep);
        return Result.Success(new SagaExecution(
            saga.SagaType, SagaStatus.Compensated, [], failedStep, failure));
    }

    private static ISagaStep<TState> FindStep<TState>(ISaga<TState> saga, string name)
        where TState : class
    {
        foreach (ISagaStep<TState> step in saga.Steps)
        {
            if (string.Equals(step.Name, name, StringComparison.Ordinal))
            {
                return step;
            }
        }

        throw new InvalidOperationException(
            $"Saga '{saga.SagaType}' recorded step '{name}' as completed but no longer defines it. "
            + "Saga definitions must not change shape while instances are in flight.");
    }
}

/// <summary>
/// Saga log messages. Step names and error codes only — saga state can carry
/// PHI and is never logged (ADR-015).
/// </summary>
internal static partial class SagaLog
{
    [LoggerMessage(EventId = 9001, Level = LogLevel.Information,
        Message = "Saga {SagaType} completed all {StepCount} steps")]
    internal static partial void Completed(ILogger logger, string sagaType, int stepCount);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning,
        Message = "Saga {SagaType} step {StepName} failed with {ErrorCode}; compensating")]
    internal static partial void StepFailed(ILogger logger, string sagaType, string stepName, string errorCode);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Warning,
        Message = "Saga {SagaType} step {StepName} threw; compensating")]
    internal static partial void StepThrew(ILogger logger, string sagaType, string stepName, Exception exception);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Information,
        Message = "Saga {SagaType} compensated cleanly after {FailedStep} failed")]
    internal static partial void Compensated(ILogger logger, string sagaType, string failedStep);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Warning,
        Message = "Saga {SagaType} compensation for {StepName} threw")]
    internal static partial void CompensationThrew(
        ILogger logger, string sagaType, string stepName, Exception exception);

    [LoggerMessage(EventId = 9006, Level = LogLevel.Critical,
        Message = "ESCALATION: saga {SagaType} compensation for {StepName} FAILED with {ErrorCode}. "
                + "{AppliedStepCount} step(s) remain applied and cannot be withdrawn automatically. "
                + "Manual intervention required.")]
    internal static partial void CompensationFailed(
        ILogger logger, string sagaType, string stepName, int appliedStepCount, string errorCode);
}
