using System;
using System.Threading;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Core.Correlation;

/// <summary>
/// Async-local ambient correlation context, mirroring
/// <see cref="Tenancy.TenantContextAccessor"/>. Register as a singleton:
/// the state rides the execution context, not the instance.
/// </summary>
public sealed class CorrelationContextAccessor : ICorrelationContextAccessor
{
    private static readonly AsyncLocal<ICorrelationContext?> Current_ = new();

    /// <inheritdoc />
    public ICorrelationContext? Current => Current_.Value;

    /// <inheritdoc />
    public IDisposable Push(ICorrelationContext context)
    {
        Guard.NotNull(context, nameof(context));
        ICorrelationContext? previous = Current_.Value;
        Current_.Value = context;
        return new PopScope(previous);
    }

    private sealed class PopScope : IDisposable
    {
        private readonly ICorrelationContext? _previous;
        private bool _disposed;

        internal PopScope(ICorrelationContext? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Current_.Value = _previous;
            _disposed = true;
        }
    }
}
