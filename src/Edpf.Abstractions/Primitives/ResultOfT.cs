using System;

namespace Edpf.Abstractions.Primitives;

/// <summary>
/// A <see cref="Result"/> that carries a value on success (Z.3 rule 8).
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
public sealed class Result<T> : Result
{
    private readonly T _value;

    private Result(T value)
        : base(null)
        => _value = value;

    private Result(Error error)
        : base(error)
        => _value = default!;

    /// <summary>
    /// The success value.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Accessed on a failed result. Check <see cref="Result.IsSuccess"/> first,
    /// or consume via <see cref="Match{TOut}(Func{T, TOut}, Func{Error, TOut})"/>.
    /// </exception>
    public T Value
        => IsSuccess
            ? _value
            : throw new InvalidOperationException(
                "Result value accessed on a failure. Error: " + Error);

    /// <summary>Creates a successful result. Named alternate for the implicit conversion.</summary>
    /// <param name="value">The value to carry.</param>
    public static Result<T> FromValue(T value) => new(value);

    /// <summary>Creates a failed result. Named alternate for the implicit conversion.</summary>
    /// <param name="error">The failure. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static Result<T> FromError(Error error)
        => new(error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Wraps a value as a successful result.</summary>
    /// <param name="value">The value to carry.</param>
    public static implicit operator Result<T>(T value) => FromValue(value);

    /// <summary>Wraps an error as a failed result.</summary>
    /// <param name="error">The failure.</param>
    public static implicit operator Result<T>(Error error) => FromError(error);

    /// <summary>
    /// Attempts to read the value without throwing.
    /// </summary>
    /// <param name="value">The value when successful; default otherwise.</param>
    /// <returns>True when this result is a success.</returns>
    public bool TryGetValue(out T value)
    {
        value = _value;
        return IsSuccess;
    }

    /// <summary>
    /// Transforms the value on success; propagates the error on failure.
    /// </summary>
    /// <typeparam name="TOut">The transformed value type.</typeparam>
    /// <param name="map">The transformation. Not invoked on failure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        if (map is null)
        {
            throw new ArgumentNullException(nameof(map));
        }

        return IsSuccess ? Result<TOut>.FromValue(map(_value)) : Result<TOut>.FromError(Error!);
    }

    /// <summary>
    /// Chains a further result-producing operation on success; propagates the
    /// error on failure (monadic bind).
    /// </summary>
    /// <typeparam name="TOut">The value type of the chained operation.</typeparam>
    /// <param name="bind">The chained operation. Not invoked on failure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bind"/> is null.</exception>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind)
    {
        if (bind is null)
        {
            throw new ArgumentNullException(nameof(bind));
        }

        return IsSuccess ? bind(_value) : Result<TOut>.FromError(Error!);
    }

    /// <summary>
    /// Invokes <paramref name="onSuccess"/> with the value or
    /// <paramref name="onFailure"/> with the error, returning the outcome.
    /// </summary>
    /// <typeparam name="TOut">The type produced by either branch.</typeparam>
    /// <param name="onSuccess">Branch taken on success; receives the value.</param>
    /// <param name="onFailure">Branch taken on failure; receives the error.</param>
    /// <exception cref="ArgumentNullException">Either branch is null.</exception>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        if (onSuccess is null)
        {
            throw new ArgumentNullException(nameof(onSuccess));
        }

        if (onFailure is null)
        {
            throw new ArgumentNullException(nameof(onFailure));
        }

        return IsSuccess ? onSuccess(_value) : onFailure(Error!);
    }
}
