using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.RealTime;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.RealTime;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.RealTime;

/// <summary>
/// The real-time platform. A socket is a read that stays open, so everything
/// the query path enforces has to hold here — and none of it is enforced by a
/// transport, which has no idea tenants exist.
/// </summary>
public sealed class RealTimeHubTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private const string Dashboard = "ward-dashboard";
    private const string CriticalResults = "critical-results";
    private const string Scope = "clinical.read";

    private readonly TenantContextAccessor _tenants = new();
    private readonly RecordingTransport _transport = new();
    private readonly RecordingEscalator _escalator = new();
    private readonly FakeClock _clock = new();

    private static RealTimeChannel[] Channels() =>
    [
        new(Dashboard, DataClassificationLevel.Internal, DeliveryGuarantee.BestEffort, Scope),
        new(CriticalResults, DataClassificationLevel.Phi, DeliveryGuarantee.RequiresAcknowledgement, Scope),
    ];

    private TenantScopedHub CreateHub()
        => new(Channels(), _transport, _tenants, _clock, _escalator);

    private IDisposable ActAs(Guid tenantId)
        => _tenants.Push(new TenantDescriptor(
            tenantId, "tenant", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()));

    // ── tenancy and scope ─────────────────────────────────────────────────

    [Fact]
    public void Subscribe_WithNoResolvedTenant_IsRefused()
    {
        Result subscribed = CreateHub().Subscribe(Dashboard, "conn-1", [Scope]);

        Assert.True(subscribed.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, subscribed.Error!.Code);
    }

    [Fact]
    public void Subscribe_WithoutTheRequiredScope_LooksExactlyLikeAMissingChannel()
    {
        // Otherwise a client enumerates the deployment's channel names by
        // watching which refusals differ from which.
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            Result unauthorized = hub.Subscribe(Dashboard, "conn-1", ["some.other.scope"]);
            Result nonexistent = hub.Subscribe("no-such-channel", "conn-1", [Scope]);

            Assert.True(unauthorized.IsFailure);
            Assert.True(nonexistent.IsFailure);
            Assert.Equal(nonexistent.Error!.Code, unauthorized.Error!.Code);
            Assert.Equal(nonexistent.Error.Message, unauthorized.Error.Message);
            Assert.Equal(nonexistent.Error.Category, unauthorized.Error.Category);
        }
    }

    [Fact]
    public async Task PublishAsync_NeverReachesAnotherTenantsConnection()
    {
        // SignalR would fan this out to every connection in the process. The
        // transport has no concept of a tenant, so the hub must.
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            hub.Subscribe(Dashboard, "conn-tenant-a", [Scope]);
        }

        using (ActAs(TenantB))
        {
            hub.Subscribe(Dashboard, "conn-tenant-b", [Scope]);

            Result<int> published = await hub.PublishAsync(
                Dashboard, "bed 4 free", DataClassificationLevel.Internal, default);

            Assert.Equal(1, published.Value);
        }

        Assert.All(_transport.Pushes, push => Assert.Equal("conn-tenant-b", push.ConnectionId));
    }

    [Fact]
    public async Task PublishAsync_WithNoResolvedTenant_IsRefused()
    {
        Result<int> published = await CreateHub().PublishAsync(
            Dashboard, "x", DataClassificationLevel.Internal, default);

        Assert.True(published.IsFailure);
        Assert.Empty(_transport.Pushes);
    }

    // ── the classification ceiling ────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_AboveTheChannelCeiling_IsRefused()
    {
        // A ward dashboard hangs on a corridor wall. It carries "bed 4 free",
        // not a diagnosis, and the ceiling is what makes that structural
        // rather than a convention the next developer has not read.
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            hub.Subscribe(Dashboard, "conn-1", [Scope]);

            Result<int> published = await hub.PublishAsync(
                Dashboard, "Mr Smith, oncology", DataClassificationLevel.Phi, default);

            Assert.True(published.IsFailure);
            Assert.Equal(ErrorCodes.ChannelClassificationExceeded, published.Error!.Code);
        }

        Assert.Empty(_transport.Pushes);
    }

    [Fact]
    public async Task PublishAsync_RefusalDoesNotQuoteThePayload()
    {
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            Result<int> published = await hub.PublishAsync(
                Dashboard, "Mr Smith, oncology", DataClassificationLevel.Phi, default);

            Assert.DoesNotContain("Smith", published.Error!.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PublishAsync_OfPhi_IsAllowedOnAChannelDeclaredForIt()
    {
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            hub.Subscribe(CriticalResults, "conn-1", [Scope]);

            Result<int> published = await hub.PublishAsync(
                CriticalResults, "potassium 6.9", DataClassificationLevel.Phi, default);

            Assert.True(published.IsSuccess);
            Assert.Equal(1, published.Value);
        }
    }

    // ── the acknowledgement rule ──────────────────────────────────────────

    [Fact]
    public void Hub_RefusesAnAcknowledgedChannelWithNoEscalator()
    {
        // A guarantee with no mechanism behind it is worse than best-effort
        // honestly labelled, so this fails at composition (ADR-014).
        Assert.Throws<ArgumentException>(
            () => new TenantScopedHub(Channels(), _transport, _tenants, _clock, escalator: null));
    }

    [Fact]
    public async Task CriticalMessage_PushedButNotAcknowledged_Escalates()
    {
        // The whole point. The socket accepted the frame; that says nothing
        // about whether a human saw it, and a platform reporting it as
        // delivered has told a clinician something false about a patient.
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            hub.Subscribe(CriticalResults, "conn-1", [Scope]);

            Result<int> published = await hub.PublishAsync(
                CriticalResults, "potassium 6.9", DataClassificationLevel.Phi, default);

            Assert.Equal(1, published.Value);
            Assert.Equal(1, hub.PendingAcknowledgementCount);

            _clock.Advance(TimeSpan.FromMinutes(5));

            Assert.Equal(1, await hub.EscalateOverdueAsync(default));
        }

        Assert.Single(_escalator.Escalated);
        Assert.Equal(TenantA, _escalator.Escalated[0].TenantId);
    }

    [Fact]
    public async Task CriticalMessage_Acknowledged_DoesNotEscalate()
    {
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            hub.Subscribe(CriticalResults, "conn-1", [Scope]);
            await hub.PublishAsync(CriticalResults, "potassium 6.9", DataClassificationLevel.Phi, default);

            string messageId = _transport.Pushes[0].Message.MessageId;
            Assert.True(hub.Acknowledge(messageId).IsSuccess);

            _clock.Advance(TimeSpan.FromMinutes(5));

            Assert.Equal(0, await hub.EscalateOverdueAsync(default));
        }

        Assert.Empty(_escalator.Escalated);
    }

    [Fact]
    public async Task CriticalMessage_WithNobodyListening_StillEscalates()
    {
        // No subscriber at all. A best-effort channel drops this; a critical
        // one must not, and "zero delivered" is not an error the caller has to
        // remember to check.
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            Result<int> published = await hub.PublishAsync(
                CriticalResults, "potassium 6.9", DataClassificationLevel.Phi, default);

            Assert.Equal(0, published.Value);

            _clock.Advance(TimeSpan.FromMinutes(5));

            Assert.Equal(1, await hub.EscalateOverdueAsync(default));
        }
    }

    [Fact]
    public async Task CriticalMessage_ToAClosedConnection_Escalates()
    {
        // The browser tab was closed and the socket had not noticed yet. This
        // is the common case, not the exotic one.
        _transport.FailingConnections.Add("conn-1");
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            hub.Subscribe(CriticalResults, "conn-1", [Scope]);

            Result<int> published = await hub.PublishAsync(
                CriticalResults, "potassium 6.9", DataClassificationLevel.Phi, default);

            Assert.Equal(0, published.Value);

            _clock.Advance(TimeSpan.FromMinutes(5));
            await hub.EscalateOverdueAsync(default);
        }

        Assert.Single(_escalator.Escalated);
    }

    [Fact]
    public async Task BestEffortMessage_IsNeverHeldForAcknowledgement()
    {
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            await hub.PublishAsync(Dashboard, "bed 4 free", DataClassificationLevel.Internal, default);

            Assert.Equal(0, hub.PendingAcknowledgementCount);
        }
    }

    [Fact]
    public async Task Acknowledge_ByAnotherTenant_DoesNotClearTheAlert()
    {
        // Otherwise any tenant could silence another's critical result by
        // guessing a message id.
        TenantScopedHub hub = CreateHub();
        string messageId;

        using (ActAs(TenantA))
        {
            hub.Subscribe(CriticalResults, "conn-1", [Scope]);
            await hub.PublishAsync(CriticalResults, "potassium 6.9", DataClassificationLevel.Phi, default);
            messageId = _transport.Pushes[0].Message.MessageId;
        }

        using (ActAs(TenantB))
        {
            Assert.True(hub.Acknowledge(messageId).IsFailure);
        }

        Assert.Equal(1, hub.PendingAcknowledgementCount);
    }

    [Fact]
    public async Task Disconnect_RemovesTheConnectionFromDelivery()
    {
        // A connection left in the table is one the hub still counts as a
        // subscriber, which is how a deadline gets satisfied by somebody who
        // is not there.
        TenantScopedHub hub = CreateHub();

        using (ActAs(TenantA))
        {
            hub.Subscribe(Dashboard, "conn-1", [Scope]);
            hub.Disconnect("conn-1");

            Result<int> published = await hub.PublishAsync(
                Dashboard, "bed 4 free", DataClassificationLevel.Internal, default);

            Assert.Equal(0, published.Value);
        }
    }

    [Fact]
    public void Channel_RefusesToBeDeclaredWithoutAScope()
    {
        // An unscoped real-time feed is an unauthenticated read that happens
        // to arrive over a socket.
        Assert.Throws<ArgumentException>(() => new RealTimeChannel(
            "open", DataClassificationLevel.Public, DeliveryGuarantee.BestEffort, "  "));
    }

    private sealed class RecordingEscalator : IAlertEscalator
    {
        public List<(RealTimeMessage Message, Guid TenantId)> Escalated { get; } = [];

        public Task<Result> EscalateAsync(
            RealTimeMessage message, Guid tenantId, CancellationToken cancellationToken)
        {
            Escalated.Add((message, tenantId));
            return Task.FromResult(Result.Success());
        }
    }
}
