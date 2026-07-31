using System;
using System.Threading;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;

namespace Edpf.Core.Tenancy;

/// <summary>
/// Async-local ambient tenant context (C4 §12.6). Register as a singleton:
/// the state rides the execution context, not the instance.
/// </summary>
public sealed class TenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<ITenantContext?> Current_ = new();

    /// <inheritdoc />
    public ITenantContext? Current => Current_.Value;

    /// <inheritdoc />
    public IDisposable Push(ITenantContext context)
    {
        Guard.NotNull(context, nameof(context));
        ITenantContext? previous = Current_.Value;
        Current_.Value = context;
        return new PopScope(previous);
    }

    private sealed class PopScope : IDisposable
    {
        private readonly ITenantContext? _previous;
        private bool _disposed;

        internal PopScope(ITenantContext? previous) => _previous = previous;

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
