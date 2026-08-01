using System;
using System.Threading;
using Edpf.Core.Guards;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Edpf.Extensions.DependencyInjection.Hosting;

/// <summary>
/// Per-operation scope management for DI-hostile hosts — WebForms, WinForms,
/// WPF, Windows Services (Phase 04 §④ legacy host adapters). Those hosts have
/// no built-in request scope, so one is established explicitly and torn down
/// deterministically.
/// </summary>
/// <remarks>
/// Correct scoping is a security control here, not an ergonomics concern: a
/// scope that outlives its operation carries the previous operation's tenant
/// context into the next one.
/// </remarks>
public sealed class EdpfScopeAccessor(IServiceProvider rootProvider)
{
    private static readonly AsyncLocal<IServiceScope?> CurrentScope = new();
    private readonly IServiceProvider _rootProvider = Guard.NotNull(rootProvider, nameof(rootProvider));

    /// <summary>
    /// The services of the current operation scope.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No scope is open. Resolving scoped services outside an operation is a
    /// defect: there is no correct tenant context to serve.
    /// </exception>
    public IServiceProvider Current
        => CurrentScope.Value?.ServiceProvider
           ?? throw new InvalidOperationException(
               "No EDPF operation scope is open. Legacy hosts must wrap each operation in "
               + $"{nameof(EdpfScopeAccessor)}.{nameof(BeginScope)}() — resolving scoped services outside "
               + "an operation would serve another operation's tenant context.");

    /// <summary>True when an operation scope is currently open on this async flow.</summary>
    public static bool HasScope => CurrentScope.Value is not null;

    /// <summary>
    /// Opens an operation scope. Dispose it when the operation ends — in
    /// WebForms, from <c>EndRequest</c>; in WinForms/WPF, around the
    /// user-initiated operation.
    /// </summary>
    /// <returns>A handle that restores the previous scope on dispose.</returns>
    public IDisposable BeginScope()
    {
        IServiceScope? previous = CurrentScope.Value;
        IServiceScope scope = _rootProvider.CreateScope();
        CurrentScope.Value = scope;
        return new ScopeHandle(scope, previous);
    }

    private sealed class ScopeHandle(IServiceScope scope, IServiceScope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentScope.Value = previous;
            scope.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// The sanctioned service-locator escape hatch (Phase 04 §④). It exists
/// because some legacy frameworks construct types themselves and cannot be
/// given constructor injection — and it is deliberately awkward to reach and
/// noisy in the logs, so it does not become the path of least resistance.
/// </summary>
/// <remarks>
/// An architecture test asserts that no assembly outside the legacy host
/// adapters calls this type.
/// </remarks>
public sealed class EdpfServiceLocator(EdpfScopeAccessor scopeAccessor, ILogger<EdpfServiceLocator> logger)
{
    private readonly EdpfScopeAccessor _scopeAccessor = Guard.NotNull(scopeAccessor, nameof(scopeAccessor));
    private readonly ILogger _logger = Guard.NotNull(logger, nameof(logger));

    /// <summary>
    /// Resolves a service from the current operation scope, logging a warning
    /// naming the caller so the usage is visible in production telemetry.
    /// </summary>
    /// <typeparam name="TService">The service to resolve.</typeparam>
    /// <param name="callerJustification">
    /// Why constructor injection is impossible here. Recorded in the warning;
    /// required, so the escape hatch cannot be used thoughtlessly.
    /// </param>
    /// <returns>The resolved service.</returns>
    public TService ResolveWithJustification<TService>(string callerJustification)
        where TService : notnull
    {
        Guard.NotNullOrWhiteSpace(callerJustification, nameof(callerJustification));

        ServiceLocatorLog.Used(_logger, typeof(TService).Name, callerJustification);
        return _scopeAccessor.Current.GetRequiredService<TService>();
    }
}

internal static partial class ServiceLocatorLog
{
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "Service-locator escape hatch used for {ServiceType}: {Justification}. "
                + "Prefer constructor injection; this path exists only for DI-hostile legacy hosts.")]
    internal static partial void Used(ILogger logger, string serviceType, string justification);
}
