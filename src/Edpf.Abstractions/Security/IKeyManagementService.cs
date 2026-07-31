using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Security;

/// <summary>
/// Key custody (§10.1 Security; C4 §12.5): resolves data-encryption keys by
/// scope or by envelope reference, and destroys them for crypto-shredding
/// erasure (ADR-006). The key hierarchy is master → tenant KEK → DEK; raw DEKs
/// exist only inside a <see cref="KeyHandle"/>, never at rest unwrapped.
/// </summary>
public interface IKeyManagementService
{
    /// <summary>
    /// Resolves (creating on first use) the current DEK for a scope. Used by
    /// the encrypt path.
    /// </summary>
    /// <param name="scope">The key scope.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>
    /// A disposable handle over the unwrapped key, or failure with
    /// <see cref="ErrorCodes.KeyDestroyed"/> when the scope was shredded.
    /// </returns>
    Task<Result<KeyHandle>> GetCurrentAsync(KeyScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a specific key an envelope declares. Used by the decrypt path.
    /// </summary>
    /// <param name="keyId">The envelope's key id.</param>
    /// <param name="keyVersion">The envelope's key version.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>
    /// A disposable handle, or failure with
    /// <see cref="ErrorCodes.KeyDestroyed"/> when the key was shredded (the
    /// caller renders a tombstone) or <see cref="ErrorCodes.CryptoFailure"/>
    /// when it never existed.
    /// </returns>
    Task<Result<KeyHandle>> ResolveAsync(Guid keyId, int keyVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Irreversibly destroys all key material for a scope — the erasure
    /// primitive of ADR-006. Audit records referencing the subject survive
    /// (they hold tokens, not identifiers); the data becomes unrecoverable.
    /// The destruction itself is audited.
    /// </summary>
    /// <param name="scope">The scope to shred.</param>
    /// <param name="cancellationToken">Cancels the operation before it starts; destruction is atomic.</param>
    /// <returns>Success, or failure when the scope has no keys.</returns>
    Task<Result> DestroyAsync(KeyScope scope, CancellationToken cancellationToken);
}
