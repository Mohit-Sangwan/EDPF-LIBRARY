using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Security;

/// <summary>
/// The algorithm registry of ADR-007 (C4 §12.5). Encrypt always uses
/// <see cref="Current"/>; decrypt always resolves the algorithm the envelope
/// declares. Adding a post-quantum successor is a <see cref="Register"/> call
/// plus configuration — no schema change, no data migration, no downtime.
/// </summary>
public interface IAlgorithmRegistry
{
    /// <summary>The algorithm used for all new encryptions.</summary>
    ISymmetricAlgorithm Current { get; }

    /// <summary>
    /// Resolves the algorithm an envelope declares.
    /// </summary>
    /// <param name="algorithmId">The envelope's algorithm id.</param>
    /// <returns>
    /// The algorithm, or failure with <see cref="ErrorCodes.CryptoFailure"/>
    /// when the id is unknown — never a silent fallback.
    /// </returns>
    Result<ISymmetricAlgorithm> Resolve(short algorithmId);

    /// <summary>
    /// Registers an algorithm. Ids are stable forever; re-registering an
    /// existing id is a defect and throws.
    /// </summary>
    /// <param name="algorithm">The algorithm to register.</param>
    /// <exception cref="System.InvalidOperationException">The id is already registered.</exception>
    void Register(ISymmetricAlgorithm algorithm);
}
