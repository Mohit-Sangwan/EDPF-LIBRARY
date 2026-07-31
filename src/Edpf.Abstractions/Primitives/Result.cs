using System;

namespace Edpf.Abstractions.Primitives;

/// <summary>
/// Explicit success/failure without exception-as-control-flow (Z.3 rule 8):
/// expected failures travel as <see cref="Error"/>; exceptions are reserved
/// for exceptional conditions.
/// </summary>
public class Result
{
    private static readonly Result CachedSuccess = new(null);

    /// <summary>Initializes the result. A null <paramref name="error"/> means success.</summary>
    /// <param name="error">The failure, or null for success.</param>
    private protected Result(Error? error) => Error = error;

    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>True when the operation failed.</summary>
    public bool IsFailure => Error is not null;

    /// <summary>The failure, or null when <see cref="IsSuccess"/>.</summary>
    public Error? Error { get; }

    /// <summary>A successful result.</summary>
    public static Result Success() => CachedSuccess;

    /// <summary>A failed result carrying <paramref name="error"/>.</summary>
    /// <param name="error">The failure. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static Result Failure(Error error)
        => new(error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>A successful result carrying <paramref name="value"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    public static Result<T> Success<T>(T value) => Result<T>.FromValue(value);

    /// <summary>A failed generic result carrying <paramref name="error"/>.</summary>
    /// <typeparam name="T">The value type of the failed operation.</typeparam>
    /// <param name="error">The failure. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static Result<T> Failure<T>(Error error) => Result<T>.FromError(error);

    /// <summary>
    /// Invokes <paramref name="onSuccess"/> or <paramref name="onFailure"/> and
    /// returns the outcome — the exhaustive way to consume a result.
    /// </summary>
    /// <typeparam name="TOut">The type produced by either branch.</typeparam>
    /// <param name="onSuccess">Branch taken when <see cref="IsSuccess"/>.</param>
    /// <param name="onFailure">Branch taken when <see cref="IsFailure"/>; receives the error.</param>
    /// <exception cref="ArgumentNullException">Either branch is null.</exception>
    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        if (onSuccess is null)
        {
            throw new ArgumentNullException(nameof(onSuccess));
        }

        if (onFailure is null)
        {
            throw new ArgumentNullException(nameof(onFailure));
        }

        return IsSuccess ? onSuccess() : onFailure(Error!);
    }
}
