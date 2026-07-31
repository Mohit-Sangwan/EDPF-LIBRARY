using System.Collections.Concurrent;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Security;

/// <summary>
/// The ADR-007 algorithm registry (C4 §12.5). Encrypt always uses
/// <see cref="Current"/>; decrypt resolves whatever the envelope declares.
/// An unknown id is a hard failure, never a silent fallback.
/// </summary>
public sealed class AlgorithmRegistry : IAlgorithmRegistry
{
    private readonly ConcurrentDictionary<short, ISymmetricAlgorithm> _algorithms = new();
    private readonly ISymmetricAlgorithm _current;

    /// <summary>Initializes the registry with AES-256-GCM as the current algorithm.</summary>
    public AlgorithmRegistry()
    {
        var aes = new AesGcmAlgorithm();
        _algorithms[aes.Id] = aes;
        _current = aes;
    }

    public ISymmetricAlgorithm Current => _current;

    public Result<ISymmetricAlgorithm> Resolve(short algorithmId)
        => _algorithms.TryGetValue(algorithmId, out var algorithm)
            ? Result.Success(algorithm)
            : Result.Failure<ISymmetricAlgorithm>(new Error(
                ErrorCodes.CryptoFailure,
                "Envelope declares an unregistered algorithm id.",
                ErrorCategory.Security));

    public void Register(ISymmetricAlgorithm algorithm)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        if (!_algorithms.TryAdd(algorithm.Id, algorithm))
        {
            throw new InvalidOperationException(
                $"Algorithm id {algorithm.Id} is already registered; ids are stable forever (ADR-007).");
        }
    }
}
