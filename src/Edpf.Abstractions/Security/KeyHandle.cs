using System;

namespace Edpf.Abstractions.Security;

/// <summary>
/// An unwrapped data-encryption key held in memory for the duration of a
/// cryptographic operation. Dispose zeroes the material — key bytes must not
/// linger on the heap (Phase 00 data-classification handling rules: PHI keys
/// are Confidential in memory).
/// </summary>
public sealed class KeyHandle : IDisposable
{
    private readonly byte[] _material;
    private bool _disposed;

    /// <summary>
    /// Initializes a handle over unwrapped key material.
    /// </summary>
    /// <param name="keyId">The key id (appears in envelopes).</param>
    /// <param name="keyVersion">The key version (appears in envelopes).</param>
    /// <param name="material">The raw key bytes. The handle takes ownership and zeroes them on dispose.</param>
    /// <exception cref="ArgumentNullException"><paramref name="material"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="material"/> is empty.</exception>
    public KeyHandle(Guid keyId, int keyVersion, byte[] material)
    {
        if (material is null)
        {
            throw new ArgumentNullException(nameof(material));
        }

        if (material.Length == 0)
        {
            throw new ArgumentException("Key material must not be empty.", nameof(material));
        }

        KeyId = keyId;
        KeyVersion = keyVersion;
        _material = material;
    }

    /// <summary>The key id.</summary>
    public Guid KeyId { get; }

    /// <summary>The key version.</summary>
    public int KeyVersion { get; }

    /// <summary>
    /// The raw key material. Valid only until <see cref="Dispose"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The handle has been disposed.</exception>
    public byte[] Material
        => _disposed
            ? throw new ObjectDisposedException(nameof(KeyHandle))
            : _material;

    /// <summary>Zeroes the key material and invalidates the handle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Array.Clear(_material, 0, _material.Length);
        _disposed = true;
    }
}
