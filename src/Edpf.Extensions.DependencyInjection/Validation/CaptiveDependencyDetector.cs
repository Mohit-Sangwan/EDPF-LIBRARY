using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Edpf.Core.Guards;
using Microsoft.Extensions.DependencyInjection;

namespace Edpf.Extensions.DependencyInjection.Validation;

/// <summary>
/// Detects captive dependencies — a singleton holding a scoped service
/// (ADR-014). In EDPF this is not a style issue: a singleton that captures
/// the scoped <c>ITenantContext</c> keeps one tenant's context alive for
/// every subsequent request, which is a cross-tenant data breach. Detection
/// is therefore mandatory rather than advisory.
/// </summary>
/// <remarks>
/// This is a static sweep of the service collection, so it reports violations
/// for services that no test happens to resolve. The container's own
/// <c>ValidateScopes</c> catches the same class of fault only when the
/// offending service is actually constructed — necessary, but not sufficient.
/// </remarks>
public static class CaptiveDependencyDetector
{
    /// <summary>
    /// Sweeps every singleton registration for scoped constructor dependencies.
    /// </summary>
    /// <param name="services">The populated service collection.</param>
    /// <returns>Every violation found; empty means the graph is safe.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IReadOnlyList<CaptiveDependency> Detect(IServiceCollection services)
    {
        Guard.NotNull(services, nameof(services));

        Dictionary<Type, ServiceLifetime> lifetimes = BuildLifetimeIndex(services);
        var violations = new List<CaptiveDependency>();

        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.Lifetime != ServiceLifetime.Singleton)
            {
                continue;
            }

            Type? implementation = descriptor.ImplementationType;
            if (implementation is null || implementation.IsAbstract || implementation.IsInterface)
            {
                // Instance and factory registrations cannot be inspected
                // statically — the factory body is opaque. Those are covered
                // by the container's ValidateScopes at build time.
                continue;
            }

            foreach (ConstructorInfo constructor in implementation.GetConstructors(
                BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    Type dependency = UnwrapCollection(parameter.ParameterType);

                    if (lifetimes.TryGetValue(dependency, out ServiceLifetime lifetime)
                        && lifetime == ServiceLifetime.Scoped)
                    {
                        violations.Add(new CaptiveDependency(
                            descriptor.ServiceType, implementation, dependency));
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Throws when any singleton captures a scoped service.
    /// </summary>
    /// <param name="services">The populated service collection.</param>
    /// <exception cref="InvalidOperationException">
    /// One or more captive dependencies exist. The message names every
    /// offending pair — a graph fault should be fixable from the exception
    /// alone.
    /// </exception>
    public static void ThrowIfAny(IServiceCollection services)
    {
        IReadOnlyList<CaptiveDependency> violations = Detect(services);
        if (violations.Count == 0)
        {
            return;
        }

        string detail = string.Join(Environment.NewLine, violations.Select(v => "  - " + v));
        throw new InvalidOperationException(
            "Captive dependency detected (ADR-014): a singleton captures a scoped service, which in a "
            + "multi-tenant framework leaks one request's tenant context into every later request."
            + Environment.NewLine + detail);
    }

    private static Dictionary<Type, ServiceLifetime> BuildLifetimeIndex(IServiceCollection services)
    {
        var index = new Dictionary<Type, ServiceLifetime>();

        foreach (ServiceDescriptor descriptor in services)
        {
            // Last registration wins in MEDI for a single resolve, and a
            // scoped registration anywhere makes the service unsafe to
            // capture — so scoped is sticky in this index.
            if (index.TryGetValue(descriptor.ServiceType, out ServiceLifetime existing)
                && existing == ServiceLifetime.Scoped)
            {
                continue;
            }

            index[descriptor.ServiceType] = descriptor.Lifetime;
        }

        return index;
    }

    private static Type UnwrapCollection(Type parameterType)
    {
        if (!parameterType.IsGenericType)
        {
            return parameterType;
        }

        Type definition = parameterType.GetGenericTypeDefinition();

        // IEnumerable<T>, IReadOnlyList<T> and friends inject every
        // registration of T, so capturing one of those captures T's lifetime.
        if (definition == typeof(IEnumerable<>)
            || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IReadOnlyList<>)
            || definition == typeof(IList<>)
            || definition == typeof(ICollection<>))
        {
            return parameterType.GetGenericArguments()[0];
        }

        return parameterType;
    }
}

/// <summary>One singleton-captures-scoped violation.</summary>
public sealed class CaptiveDependency
{
    /// <summary>
    /// Initializes the violation record.
    /// </summary>
    /// <param name="singletonService">The registered singleton service type.</param>
    /// <param name="singletonImplementation">The implementation doing the capturing.</param>
    /// <param name="capturedScopedService">The scoped service being captured.</param>
    public CaptiveDependency(Type singletonService, Type singletonImplementation, Type capturedScopedService)
    {
        SingletonService = Guard.NotNull(singletonService, nameof(singletonService));
        SingletonImplementation = Guard.NotNull(singletonImplementation, nameof(singletonImplementation));
        CapturedScopedService = Guard.NotNull(capturedScopedService, nameof(capturedScopedService));
    }

    /// <summary>The registered singleton service type.</summary>
    public Type SingletonService { get; }

    /// <summary>The implementation whose constructor captures the scoped service.</summary>
    public Type SingletonImplementation { get; }

    /// <summary>The scoped service being captured.</summary>
    public Type CapturedScopedService { get; }

    /// <summary>Formats the violation as <c>Singleton (Impl) captures Scoped</c>.</summary>
    public override string ToString()
        => $"singleton {SingletonService.Name} ({SingletonImplementation.Name}) "
         + $"captures scoped {CapturedScopedService.Name}";
}
