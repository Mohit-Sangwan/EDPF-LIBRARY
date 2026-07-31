using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Consistency;

/// <summary>
/// Dispatches committed outbox messages to their transport (§10.1 Consistency;
/// ADR-003). Messages are enqueued in the same local transaction as the state
/// change that caused them; the dispatcher delivers them afterwards, at least
/// once — consumers are idempotent by contract (§10.5).
/// </summary>
public interface IOutboxDispatcher
{
    /// <summary>
    /// Dispatches up to <paramref name="batchSize"/> pending messages.
    /// </summary>
    /// <param name="batchSize">Maximum messages to dispatch in this sweep.</param>
    /// <param name="cancellationToken">Stops the sweep between messages.</param>
    /// <returns>The number of messages dispatched.</returns>
    Task<Result<int>> DispatchPendingAsync(int batchSize, CancellationToken cancellationToken);
}
