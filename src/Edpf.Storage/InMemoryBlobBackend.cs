using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Core.Guards;

namespace Edpf.Storage;

/// <summary>
/// Holds blobs in process memory. Tests and single-node development only.
/// </summary>
/// <remarks>
/// <para>
/// It exists mainly so the policy layer can be exercised without touching a
/// disk, which is what makes the tenancy and encryption tests fast enough to
/// run on every build rather than nightly.
/// </para>
/// <para>
/// <see cref="RawBytesAt"/> is the deliberate exception to "a store exposes no
/// internals": it lets a test read what physically landed, which is the only
/// way to assert that classified content was written as ciphertext rather than
/// merely reported as encrypted. A property nobody can observe is a property
/// nobody can verify.
/// </para>
/// </remarks>
public sealed class InMemoryBlobBackend : IBlobBackend
{
    private readonly ConcurrentDictionary<string, byte[]> _content = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _metadata =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string BackendName => "InMemory";

    /// <summary>
    /// The bytes physically stored at a rendered path, or null.
    /// </summary>
    /// <param name="renderedPath">The full tenant-prefixed path.</param>
    /// <returns>The stored bytes exactly as written — ciphertext when encrypted.</returns>
    public byte[]? RawBytesAt(string renderedPath)
        => renderedPath is not null && _content.TryGetValue(renderedPath, out byte[]? bytes) ? bytes : null;

    /// <inheritdoc />
    public Task<Result> PutAsync(BlobPath path, byte[] bytes, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(bytes, nameof(bytes));
        cancellationToken.ThrowIfCancellationRequested();

        _content[path.Value] = bytes;
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result<byte[]>> GetAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _content.TryGetValue(path.Value, out byte[]? bytes)
                ? Result<byte[]>.FromValue(bytes)
                : Result.Failure<byte[]>(NotFound()));
    }

    /// <inheritdoc />
    public Task<Result> RemoveAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();

        _metadata.TryRemove(path.Value, out _);

        return Task.FromResult(
            _content.TryRemove(path.Value, out _) ? Result.Success() : Result.Failure(NotFound()));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<string>>> ListAsync(string renderedPrefix, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(renderedPrefix, nameof(renderedPrefix));
        cancellationToken.ThrowIfCancellationRequested();

        var matches = new List<string>();
        foreach (string key in _content.Keys)
        {
            if (key.StartsWith(renderedPrefix, StringComparison.Ordinal))
            {
                matches.Add(key);
            }
        }

        return Task.FromResult(Result<IReadOnlyList<string>>.FromValue(matches));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        BlobPath path,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _metadata.TryGetValue(path.Value, out IReadOnlyDictionary<string, string>? metadata)
                ? Result<IReadOnlyDictionary<string, string>>.FromValue(metadata)
                : Result.Failure<IReadOnlyDictionary<string, string>>(NotFound()));
    }

    /// <inheritdoc />
    public Task<Result> PutMetadataAsync(
        BlobPath path,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(metadata, nameof(metadata));
        cancellationToken.ThrowIfCancellationRequested();

        _metadata[path.Value] = metadata;
        return Task.FromResult(Result.Success());
    }

    private static Error NotFound() => new(
        ErrorCodes.NotFound,
        "The requested resource was not found.",
        ErrorCategory.NotFound);
}
