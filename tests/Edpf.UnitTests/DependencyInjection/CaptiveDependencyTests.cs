using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Extensions.DependencyInjection;
using Edpf.Extensions.DependencyInjection.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edpf.UnitTests.DependencyInjection;

/// <summary>
/// Phase 04 §⑤: the captive-dependency test, which must pass **and fail
/// correctly when violated**. A singleton capturing the scoped tenant context
/// is not a style issue — it serves one tenant's context to every later
/// request.
/// </summary>
public sealed class CaptiveDependencyTests
{
    private interface IScopedThing;

    private sealed class ScopedThing : IScopedThing;

    private interface ISingletonThing;

    private sealed class WellBehavedSingleton : ISingletonThing;

    private sealed class OffendingSingleton(IScopedThing scoped) : ISingletonThing
    {
        public IScopedThing Captured { get; } = scoped;
    }

    private sealed class OffendingCollectionSingleton(IEnumerable<IScopedThing> scoped) : ISingletonThing
    {
        public IEnumerable<IScopedThing> Captured { get; } = scoped;
    }

    private static IConfiguration EmptyConfiguration =>
        new ConfigurationBuilder().Build();

    [Fact]
    public void Detect_SingletonCapturingScoped_IsReported()
    {
        var services = new ServiceCollection();
        services.AddScoped<IScopedThing, ScopedThing>();
        services.AddSingleton<ISingletonThing, OffendingSingleton>();

        IReadOnlyList<CaptiveDependency> violations = CaptiveDependencyDetector.Detect(services);

        CaptiveDependency violation = Assert.Single(violations);
        Assert.Equal(typeof(IScopedThing), violation.CapturedScopedService);
        Assert.Equal(typeof(OffendingSingleton), violation.SingletonImplementation);
    }

    [Fact]
    public void Detect_SingletonCapturingScopedCollection_IsReported()
    {
        // IEnumerable<T> injection captures T's lifetime just as surely.
        var services = new ServiceCollection();
        services.AddScoped<IScopedThing, ScopedThing>();
        services.AddSingleton<ISingletonThing, OffendingCollectionSingleton>();

        Assert.Single(CaptiveDependencyDetector.Detect(services));
    }

    [Fact]
    public void Detect_CleanGraph_ReportsNothing()
    {
        var services = new ServiceCollection();
        services.AddScoped<IScopedThing, ScopedThing>();
        services.AddSingleton<ISingletonThing, WellBehavedSingleton>();

        Assert.Empty(CaptiveDependencyDetector.Detect(services));
    }

    [Fact]
    public void ThrowIfAny_Violation_ThrowsNamingBothTypes()
    {
        var services = new ServiceCollection();
        services.AddScoped<IScopedThing, ScopedThing>();
        services.AddSingleton<ISingletonThing, OffendingSingleton>();

        var exception = Assert.Throws<InvalidOperationException>(
            () => CaptiveDependencyDetector.ThrowIfAny(services));

        // A graph fault should be fixable from the exception alone.
        Assert.Contains(nameof(OffendingSingleton), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IScopedThing), exception.Message, StringComparison.Ordinal);
        Assert.Contains("ADR-014", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfAny_CleanGraph_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddScoped<IScopedThing, ScopedThing>();
        services.AddSingleton<ISingletonThing, WellBehavedSingleton>();

        CaptiveDependencyDetector.ThrowIfAny(services);
    }

    [Fact]
    public void AddEdpfCore_ProducesAGraphWithNoCaptiveDependencies()
    {
        // The framework's own registrations must satisfy the rule they impose.
        var services = new ServiceCollection();
        services.AddEdpfCore(EmptyConfiguration);

        CaptiveDependencyDetector.ThrowIfAny(services);
    }

    [Fact]
    public void AddEdpfCore_RegistersKernelServicesAsSingletons()
    {
        var services = new ServiceCollection();

        IEdpfBuilder builder = services.AddEdpfCore(EmptyConfiguration);
        ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        Assert.Contains(EdpfModules.Core, builder.RegisteredModules);
        Assert.NotNull(provider.GetRequiredService<IClock>());
        Assert.NotNull(provider.GetRequiredService<ITenantContextAccessor>());
        Assert.NotNull(provider.GetRequiredService<ICorrelationContextAccessor>());
        provider.Dispose();
    }

    [Fact]
    public void AddEdpfCore_CalledTwice_IsIdempotent()
    {
        var services = new ServiceCollection();

        services.AddEdpfCore(EmptyConfiguration);
        IEdpfBuilder second = services.AddEdpfCore(EmptyConfiguration);

        Assert.Single(second.RegisteredModules);
        Assert.Single(services, d => d.ServiceType == typeof(IClock));
    }

    [Fact]
    public void TryRegisterModule_SameModuleTwice_ReturnsFalseSecondTime()
    {
        var services = new ServiceCollection();
        IEdpfBuilder builder = services.AddEdpfCore(EmptyConfiguration);

        Assert.True(builder.TryRegisterModule("SqlServer"));
        Assert.False(builder.TryRegisterModule("SqlServer"));
    }
}
