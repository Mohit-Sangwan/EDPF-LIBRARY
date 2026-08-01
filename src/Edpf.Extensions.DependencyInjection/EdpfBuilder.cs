using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Correlation;
using Edpf.Core.Guards;
using Edpf.Core.Tenancy;
using Edpf.Core.Time;
using Edpf.Abstractions.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edpf.Extensions.DependencyInjection;

/// <summary>Default <see cref="IEdpfBuilder"/>.</summary>
internal sealed class EdpfBuilder(IServiceCollection services, IConfiguration configuration) : IEdpfBuilder
{
    private readonly HashSet<string> _modules = new(StringComparer.Ordinal);

    public IServiceCollection Services { get; } = Guard.NotNull(services, nameof(services));

    public IConfiguration Configuration { get; } = Guard.NotNull(configuration, nameof(configuration));

    public IReadOnlyCollection<string> RegisteredModules => _modules;

    public bool TryRegisterModule(string moduleName)
        => _modules.Add(Guard.NotNullOrWhiteSpace(moduleName, nameof(moduleName)));
}

/// <summary>Entry point for EDPF registration (ADR-014).</summary>
public static class EdpfServiceCollectionExtensions
{
    /// <summary>
    /// Registers the EDPF shared kernel and returns the builder that feature
    /// modules extend.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The builder, for chaining feature modules.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static IEdpfBuilder AddEdpfCore(this IServiceCollection services, IConfiguration configuration)
    {
        Guard.NotNull(services, nameof(services));
        Guard.NotNull(configuration, nameof(configuration));

        // The builder is itself registered, so repeated calls — and feature
        // modules that defensively call AddEdpfCore — share one module set and
        // register exactly once. A fresh builder per call would silently
        // duplicate every registration.
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IEdpfBuilder)
                && descriptor.ImplementationInstance is IEdpfBuilder existing)
            {
                return existing;
            }
        }

        var builder = new EdpfBuilder(services, configuration);
        services.AddSingleton<IEdpfBuilder>(builder);
        builder.TryRegisterModule(EdpfModules.Core);

        // Lifetime policy (ADR-014), stated once here rather than rediscovered
        // per module: stateless helpers are singletons; ambient-context
        // accessors are singletons because their state rides the async
        // execution context, not the instance.
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();

        return builder;
    }
}

/// <summary>Canonical module names, so diagnostics and duplicate detection agree.</summary>
public static class EdpfModules
{
    /// <summary>The shared kernel.</summary>
    public const string Core = "Core";

    /// <summary>Configuration precedence, validation and secret custody (Phase 03).</summary>
    public const string Configuration = "Configuration";

    /// <summary>Logging, tracing, metrics and redaction (Phase 05).</summary>
    public const string Observability = "Observability";
}
