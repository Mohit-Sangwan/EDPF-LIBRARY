using System;

namespace Edpf.Abstractions.Configuration;

/// <summary>
/// Wraps a sensitive string so that leaking it requires deliberate effort
/// (Phase 03 §④). <see cref="ToString"/> returns <see cref="Redacted"/>, the
/// type refuses serialization, and rule EDPF0006 rejects logging it — which
/// makes credential leakage a build error rather than a code-review hope.
/// </summary>
/// <remarks>
/// Reading the real value requires calling <see cref="Reveal"/>, which is
/// deliberately conspicuous at a call site and in review. Equality is
/// constant-time to avoid turning a comparison into a timing oracle.
/// </remarks>
public sealed class SecretValue : IEquatable<SecretValue>, IDisposable
{
    /// <summary>What a secret renders as everywhere except <see cref="Reveal"/>.</summary>
    public const string Redacted = "***";

    private readonly char[] _value;
    private bool _disposed;

    /// <summary>
    /// Wraps <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The sensitive string. Copied; the caller's instance is untouched.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public SecretValue(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        _value = value.ToCharArray();
    }

    /// <summary>An empty secret — distinct from a missing one.</summary>
    public static SecretValue Empty { get; } = new(string.Empty);

    /// <summary>True when the wrapped value has zero length.</summary>
    public bool IsEmpty => _value.Length == 0;

    /// <summary>Length of the wrapped value. Safe to log; the value itself is not.</summary>
    public int Length => _value.Length;

    /// <summary>
    /// Returns the real value. Every call site is a deliberate, reviewable
    /// decision — prefer passing the <see cref="SecretValue"/> itself.
    /// </summary>
    /// <returns>The unwrapped secret.</returns>
    /// <exception cref="ObjectDisposedException">The secret has been disposed.</exception>
    public string Reveal()
        => _disposed
            ? throw new ObjectDisposedException(nameof(SecretValue))
            : new string(_value);

    /// <summary>Always <see cref="Redacted"/> — never the value.</summary>
    public override string ToString() => Redacted;

    /// <summary>
    /// Constant-time equality: comparison time does not depend on how many
    /// leading characters match, so it cannot be used as a timing oracle.
    /// </summary>
    /// <param name="other">The secret to compare against.</param>
    public bool Equals(SecretValue? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_value.Length != other._value.Length)
        {
            return false;
        }

        int difference = 0;
        for (int i = 0; i < _value.Length; i++)
        {
            difference |= _value[i] ^ other._value[i];
        }

        return difference == 0;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SecretValue);

    /// <summary>
    /// Returns a length-derived hash only. Hashing the content would let a
    /// secret leak through a hash-code dump or a dictionary diagnostic.
    /// </summary>
    public override int GetHashCode() => _value.Length;

    /// <summary>Overwrites the buffer and invalidates the secret.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Array.Clear(_value, 0, _value.Length);
        _disposed = true;
    }
}
