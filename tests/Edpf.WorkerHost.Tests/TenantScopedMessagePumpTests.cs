using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.WorkerHost;

namespace Edpf.WorkerHost.Tests;

/// <summary>
/// Tenancy in a host with no request (ADR-037 v1.0 addition 4).
/// </summary>
/// <remarks>
/// The Web API host makes tenancy unavoidable with a pipeline stage that runs
/// before anything touches data. A worker has no pipeline and no request, so
/// the same guarantee has to be re-established from a different source — and
/// "re-established" is exactly the kind of claim that is worth a test rather
/// than a paragraph.
/// </remarks>
public sealed class TenantScopedMessagePumpTests
{
    private static readonly Guid KnownTenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UnknownTenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly TenantContextAccessor _tenants = new();
    private readonly RecordingHandler _handler;

    public TenantScopedMessagePumpTests() => _handler = new RecordingHandler(_tenants);

    private TenantScopedMessagePump CreatePump()
        => new(_tenants, new SingleTenantStore(KnownTenant), [_handler]);

    private static QueuedMessage Message(Guid tenantId, string type = "appointment-reminder")
        => new(Guid.NewGuid(), tenantId, type, "{}");

    [Fact]
    public async Task ProcessAsync_WithNoTenantOnTheMessage_IsRefusedAndNoHandlerRuns()
    {
        // The failure this closes: a worker that treats a missing tenant as
        // "process it anyway" runs a handler as nobody. A replayed dead-letter
        // or an older producer is enough to produce one of these.
        Result processed = await CreatePump().ProcessAsync(Message(Guid.Empty), default);

        Assert.True(processed.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, processed.Error!.Code);
        Assert.Equal(0, _handler.Invocations);
    }

    [Fact]
    public async Task ProcessAsync_ForATenantThatNoLongerExists_IsRefused()
    {
        // A queue outlives a tenant. Messages sit in it across an offboarding,
        // and handling them afterwards writes data for somebody who has left.
        Result processed = await CreatePump().ProcessAsync(Message(UnknownTenant), default);

        Assert.True(processed.IsFailure);
        Assert.Equal(0, _handler.Invocations);
    }

    [Fact]
    public async Task ProcessAsync_WithNoRegisteredHandler_IsRefusedRatherThanDropped()
    {
        // Silently discarding an unrecognised message type loses data during
        // exactly the window a rolling deployment produces it.
        Result processed = await CreatePump().ProcessAsync(Message(KnownTenant, "unknown-type"), default);

        Assert.True(processed.IsFailure);
        Assert.Equal(ErrorCodes.SchemaMismatch, processed.Error!.Code);
    }

    [Fact]
    public async Task ProcessAsync_EstablishesTheTenantBeforeTheHandlerRuns()
    {
        Result processed = await CreatePump().ProcessAsync(Message(KnownTenant), default);

        Assert.True(processed.IsSuccess);
        Assert.Equal(KnownTenant, _handler.TenantSeen);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotLeaveTheTenantAmbientAfterwards()
    {
        // Ambient state that outlives its message is how the second message in
        // a batch gets processed as the first message's tenant.
        TenantScopedMessagePump pump = CreatePump();

        await pump.ProcessAsync(Message(KnownTenant), default);

        Assert.Null(_tenants.Current);
    }

    [Fact]
    public async Task ProcessAsync_AfterAFailedMessage_StillHasNoAmbientTenant()
    {
        TenantScopedMessagePump pump = CreatePump();
        _handler.ShouldFail = true;

        await pump.ProcessAsync(Message(KnownTenant), default);

        Assert.Null(_tenants.Current);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsTheHandlersFailureUnchanged()
    {
        // The pump does not swallow, retry or reclassify. Deciding what to do
        // with a failed message belongs to whatever owns the queue.
        TenantScopedMessagePump pump = CreatePump();
        _handler.ShouldFail = true;

        Result processed = await pump.ProcessAsync(Message(KnownTenant), default);

        Assert.True(processed.IsFailure);
        Assert.Equal(ErrorCodes.IntegrationFailed, processed.Error!.Code);
    }

    /// <remarks>
    /// Takes the fixture's accessor rather than constructing one. A second
    /// accessor would read a different ambient slot and always see null, and
    /// <see cref="ProcessAsync_EstablishesTheTenantBeforeTheHandlerRuns"/>
    /// would then fail for a reason that has nothing to do with the pump.
    /// </remarks>
    private sealed class RecordingHandler(ITenantContextAccessor accessor) : IMessageHandler
    {
        public string MessageType => "appointment-reminder";

        public int Invocations { get; private set; }

        public Guid? TenantSeen { get; private set; }

        public bool ShouldFail { get; set; }

        public Task<Result> HandleAsync(QueuedMessage message, CancellationToken cancellationToken)
        {
            Invocations++;
            TenantSeen = accessor.Current?.TenantId;

            return Task.FromResult(ShouldFail
                ? Result.Failure(new Error(
                    ErrorCodes.IntegrationFailed, "Downstream unavailable.", ErrorCategory.Transient))
                : Result.Success());
        }
    }

    private sealed class SingleTenantStore(Guid known) : ITenantStore
    {
        public Task<Result<TenantDescriptor>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
            => Task.FromResult(tenantId == known
                ? Result<TenantDescriptor>.FromValue(new TenantDescriptor(
                    known, "demo", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()))
                : Result.Failure<TenantDescriptor>(new Error(
                    ErrorCodes.TenantScopeViolation,
                    "The requested resource was not found.",
                    ErrorCategory.NotFound)));
    }
}
