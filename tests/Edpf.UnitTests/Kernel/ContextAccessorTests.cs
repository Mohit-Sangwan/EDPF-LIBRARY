using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Correlation;
using Edpf.Core.Tenancy;
using Edpf.Core.Time;

namespace Edpf.UnitTests.Kernel;

public sealed class TenantContextAccessorTests
{
    private static TenantDescriptor Tenant(string name = "t") => new(
        Guid.NewGuid(), name, "in-south-1", TenantIsolationMode.SharedSchema, Guid.NewGuid());

    [Fact]
    public void Current_OutsideScope_IsNull()
    {
        var accessor = new TenantContextAccessor();

        Assert.Null(accessor.Current);
    }

    [Fact]
    public void Push_InsideScope_ExposesContext()
    {
        var accessor = new TenantContextAccessor();
        TenantDescriptor tenant = Tenant();

        using (accessor.Push(tenant))
        {
            Assert.Same(tenant, accessor.Current);
        }

        Assert.Null(accessor.Current);
    }

    [Fact]
    public void Push_Nested_RestoresPreviousOnDispose()
    {
        var accessor = new TenantContextAccessor();
        TenantDescriptor outer = Tenant("outer");
        TenantDescriptor inner = Tenant("inner");

        using (accessor.Push(outer))
        {
            using (accessor.Push(inner))
            {
                Assert.Same(inner, accessor.Current);
            }

            Assert.Same(outer, accessor.Current);
        }
    }

    [Fact]
    public async Task Push_ParallelFlows_DoNotLeakAcrossAsyncContexts()
    {
        var accessor = new TenantContextAccessor();
        TenantDescriptor tenantA = Tenant("a");
        TenantDescriptor tenantB = Tenant("b");

        Task flowA = Task.Run(async () =>
        {
            using (accessor.Push(tenantA))
            {
                await Task.Delay(20);
                Assert.Same(tenantA, accessor.Current);
            }
        });

        Task flowB = Task.Run(async () =>
        {
            using (accessor.Push(tenantB))
            {
                await Task.Delay(20);
                Assert.Same(tenantB, accessor.Current);
            }
        });

        await Task.WhenAll(flowA, flowB);
    }
}

public sealed class CorrelationContextTests
{
    [Fact]
    public void StartNew_Always_ProducesDistinctCorrelationAndRequestIds()
    {
        var context = CorrelationContext.StartNew();

        Assert.NotEqual(context.CorrelationId, context.RequestId);
        Assert.Null(context.CausationId);
    }

    [Fact]
    public void Continue_WithInboundId_KeepsCorrelationAssignsNewRequest()
    {
        var context = CorrelationContext.Continue("corr-123", "req-1");

        Assert.Equal("corr-123", context.CorrelationId);
        Assert.NotEqual("req-1", context.RequestId);
        Assert.Equal("req-1", context.CausationId);
    }

    [Fact]
    public void Continue_BlankInbound_Throws()
    {
        Assert.Throws<ArgumentException>(() => CorrelationContext.Continue("  "));
    }

    [Fact]
    public void Push_Accessor_RoundTrips()
    {
        var accessor = new CorrelationContextAccessor();
        var context = CorrelationContext.StartNew();

        using (accessor.Push(context))
        {
            Assert.Same(context, accessor.Current);
        }

        Assert.Null(accessor.Current);
    }
}

public sealed class SystemClockTests
{
    [Fact]
    public void UtcNow_Always_IsUtcAndCurrent()
    {
        SystemClock clock = SystemClock.Instance;

        DateTimeOffset now = clock.UtcNow;

        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.True((DateTimeOffset.UtcNow - now).Duration() < TimeSpan.FromSeconds(5));
    }
}
