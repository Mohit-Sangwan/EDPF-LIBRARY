using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Data;

/// <summary>
/// Commits a business operation atomically within one store (§10.1 Repository).
/// Per ADR-003 there is no cross-store transaction: one local ACID commit,
/// with cross-store effects riding the outbox in that same commit.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Commits all staged changes — entities, outbox messages and audit
    /// records — in one local transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancels before the commit; the commit itself is atomic.</param>
    /// <returns>
    /// Success once durable; failure with
    /// <see cref="ErrorCodes.ConcurrencyConflict"/>,
    /// <see cref="ErrorCodes.Duplicate"/> or
    /// <see cref="ErrorCodes.TransactionFailed"/> otherwise.
    /// </returns>
    Task<Result> CommitAsync(CancellationToken cancellationToken);
}
