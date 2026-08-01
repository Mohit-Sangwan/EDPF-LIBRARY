using Edpf.Abstractions.Consistency;
using Edpf.Abstractions.Primitives;
using Edpf.Data.Consistency;
using Microsoft.Extensions.Logging.Abstractions;

namespace Edpf.UnitTests.Data;

/// <summary>
/// Phase 09 §⑤: fail a saga at each step and assert full compensation; fail a
/// *compensation* and assert escalation fires.
/// </summary>
public sealed class SagaCoordinatorTests
{
    private sealed class OrderState
    {
        public List<string> Log { get; } = [];
    }

    private sealed class RecordingStep(
        string name,
        bool failExecute = false,
        bool failCompensate = false,
        bool throwOnExecute = false,
        bool throwOnCompensate = false) : ISagaStep<OrderState>
    {
        public string Name { get; } = name;

        public Task<Result> ExecuteAsync(OrderState state, CancellationToken cancellationToken)
        {
            if (throwOnExecute)
            {
                throw new InvalidOperationException("boom in " + Name);
            }

            state.Log.Add("do:" + Name);
            return Task.FromResult(failExecute
                ? Result.Failure(new Error("EDPF-TX-4001", "step failed", ErrorCategory.Internal))
                : Result.Success());
        }

        public Task<Result> CompensateAsync(OrderState state, CancellationToken cancellationToken)
        {
            if (throwOnCompensate)
            {
                throw new InvalidOperationException("boom compensating " + Name);
            }

            state.Log.Add("undo:" + Name);
            return Task.FromResult(failCompensate
                ? Result.Failure(new Error("EDPF-TX-4001", "compensation failed", ErrorCategory.Internal))
                : Result.Success());
        }
    }

    private sealed class OrderSaga(params ISagaStep<OrderState>[] steps) : ISaga<OrderState>
    {
        public string SagaType => "PlaceOrder";

        public IReadOnlyList<ISagaStep<OrderState>> Steps { get; } = steps;

        public TimeSpan Timeout => TimeSpan.FromMinutes(5);
    }

    private static SagaCoordinator Coordinator => new(NullLogger<SagaCoordinator>.Instance);

    [Fact]
    public async Task RunAsync_AllStepsSucceed_CompletesInOrder()
    {
        var state = new OrderState();
        var saga = new OrderSaga(new RecordingStep("reserve"), new RecordingStep("charge"), new RecordingStep("ship"));

        Result<SagaExecution> result = await Coordinator.RunAsync(saga, state, CancellationToken.None);

        Assert.Equal(SagaStatus.Completed, result.Value.Status);
        Assert.Equal(["do:reserve", "do:charge", "do:ship"], state.Log);
        Assert.False(result.Value.RequiresEscalation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunAsync_FailureAtAnyStep_CompensatesEveryCompletedStepInReverse(int failingIndex)
    {
        var state = new OrderState();
        var steps = new ISagaStep<OrderState>[]
        {
            new RecordingStep("reserve", failExecute: failingIndex == 0),
            new RecordingStep("charge", failExecute: failingIndex == 1),
            new RecordingStep("ship", failExecute: failingIndex == 2),
        };

        Result<SagaExecution> result = await Coordinator.RunAsync(new OrderSaga(steps), state, CancellationToken.None);

        Assert.Equal(SagaStatus.Compensated, result.Value.Status);

        // Every step that ran is undone, most recent first.
        string[] expectedUndo = failingIndex switch
        {
            0 => [],
            1 => ["undo:reserve"],
            _ => ["undo:charge", "undo:reserve"],
        };
        Assert.Equal(expectedUndo, state.Log.Where(e => e.StartsWith("undo:", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task RunAsync_CompensationFails_EscalatesAndStopsImmediately()
    {
        // The case everyone forgets (Phase 09 §④). Compensation for 'charge'
        // fails, so 'reserve' is deliberately NOT compensated: continuing
        // would layer more changes onto an already-inconsistent state and
        // make the manual repair harder.
        var state = new OrderState();
        var saga = new OrderSaga(
            new RecordingStep("reserve"),
            new RecordingStep("charge", failCompensate: true),
            new RecordingStep("ship", failExecute: true));

        Result<SagaExecution> result = await Coordinator.RunAsync(saga, state, CancellationToken.None);

        SagaExecution outcome = result.Value;
        Assert.Equal(SagaStatus.CompensationFailed, outcome.Status);
        Assert.True(outcome.RequiresEscalation);
        Assert.Equal("charge", outcome.FailedStep);

        // The report states exactly what is still applied — the information a
        // human needs to repair it.
        Assert.Equal(["reserve", "charge"], outcome.CompletedSteps);
        Assert.DoesNotContain("undo:reserve", state.Log);
    }

    [Fact]
    public async Task RunAsync_StepThrows_IsTreatedAsFailureAndCompensates()
    {
        var state = new OrderState();
        var saga = new OrderSaga(
            new RecordingStep("reserve"),
            new RecordingStep("charge", throwOnExecute: true));

        Result<SagaExecution> result = await Coordinator.RunAsync(saga, state, CancellationToken.None);

        Assert.Equal(SagaStatus.Compensated, result.Value.Status);
        Assert.Contains("undo:reserve", state.Log);
    }

    [Fact]
    public async Task RunAsync_CompensationThrows_EscalatesRatherThanPropagating()
    {
        var state = new OrderState();
        var saga = new OrderSaga(
            new RecordingStep("reserve", throwOnCompensate: true),
            new RecordingStep("charge", failExecute: true));

        Result<SagaExecution> result = await Coordinator.RunAsync(saga, state, CancellationToken.None);

        Assert.Equal(SagaStatus.CompensationFailed, result.Value.Status);
        Assert.True(result.Value.RequiresEscalation);
    }

    [Fact]
    public async Task RunAsync_NoSteps_CompletesTrivially()
    {
        Result<SagaExecution> result =
            await Coordinator.RunAsync(new OrderSaga(), new OrderState(), CancellationToken.None);

        Assert.Equal(SagaStatus.Completed, result.Value.Status);
        Assert.Empty(result.Value.CompletedSteps);
    }
}
