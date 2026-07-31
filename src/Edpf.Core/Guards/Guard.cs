using System;

namespace Edpf.Core.Guards;

/// <summary>
/// Guard clauses for public boundaries (Phase 01 shared kernel). Methods
/// return their input so guards compose with assignment. These method names
/// are registered with CA1062 (<c>.editorconfig</c>) so analysis recognises
/// guarded parameters as validated.
/// </summary>
public static class Guard
{
    /// <summary>
    /// Rejects null.
    /// </summary>
    /// <typeparam name="T">The reference type being guarded.</typeparam>
    /// <param name="value">The value to check.</param>
    /// <param name="parameterName">The caller's parameter name.</param>
    /// <returns><paramref name="value"/>, non-null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static T NotNull<T>(T? value, string parameterName)
        where T : class
        => value ?? throw new ArgumentNullException(parameterName);

    /// <summary>
    /// Rejects null or empty strings.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <param name="parameterName">The caller's parameter name.</param>
    /// <returns><paramref name="value"/>, non-empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty.</exception>
    public static string NotNullOrEmpty(string? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (value.Length == 0)
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value;
    }

    /// <summary>
    /// Rejects null, empty, or whitespace-only strings.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <param name="parameterName">The caller's parameter name.</param>
    /// <returns><paramref name="value"/>, non-blank.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is blank.</exception>
    public static string NotNullOrWhiteSpace(string? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        return value;
    }

    /// <summary>
    /// Rejects the default value of a struct (e.g. <see cref="Guid.Empty"/>,
    /// an unset <c>EntityId</c>).
    /// </summary>
    /// <typeparam name="T">The struct type being guarded.</typeparam>
    /// <param name="value">The value to check.</param>
    /// <param name="parameterName">The caller's parameter name.</param>
    /// <returns><paramref name="value"/>, non-default.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> equals <c>default(T)</c>.</exception>
    public static T NotDefault<T>(T value, string parameterName)
        where T : struct, IEquatable<T>
    {
        if (value.Equals(default))
        {
            throw new ArgumentException("Value must not be the default value.", parameterName);
        }

        return value;
    }
}
