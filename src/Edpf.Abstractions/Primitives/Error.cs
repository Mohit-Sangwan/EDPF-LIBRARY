using System;

namespace Edpf.Abstractions.Primitives;

/// <summary>
/// An expected failure, carried by <see cref="Result"/> / <see cref="Result{T}"/>
/// instead of an exception (Z.3 rule 8). Immutable.
/// </summary>
/// <remarks>
/// The <see cref="Code"/> is public and stable (§10.2); the <see cref="Message"/>
/// must never contain classified data, raw input, secrets or internals (Z.10) —
/// detail belongs in the log, keyed by <see cref="CorrelationId"/>.
/// </remarks>
public sealed class Error : IEquatable<Error>
{
    /// <summary>
    /// Initializes a new error.
    /// </summary>
    /// <param name="code">Stable EDPF error code, e.g. <see cref="ErrorCodes.NotFound"/>.</param>
    /// <param name="message">Safe, human-readable description. No classified data, ever.</param>
    /// <param name="category">Coarse classification for transport mapping.</param>
    /// <param name="correlationId">Correlation id linking this error to its log entries, if available.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="message"/> is null.</exception>
    public Error(string code, string message, ErrorCategory category, string? correlationId = null)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Category = category;
        CorrelationId = correlationId;
    }

    /// <summary>Stable EDPF error code (<c>EDPF-&lt;AREA&gt;-&lt;NNNN&gt;</c>).</summary>
    public string Code { get; }

    /// <summary>Safe, human-readable description. Never contains classified data.</summary>
    public string Message { get; }

    /// <summary>Coarse classification used for transport mapping and retry policy.</summary>
    public ErrorCategory Category { get; }

    /// <summary>Correlation id linking this error to its log entries, if available.</summary>
    public string? CorrelationId { get; }

    /// <summary>Returns a copy of this error carrying the given correlation id.</summary>
    /// <param name="correlationId">The correlation id to attach.</param>
    /// <returns>A new <see cref="Error"/> identical to this one but with <paramref name="correlationId"/>.</returns>
    public Error WithCorrelationId(string? correlationId)
        => new(Code, Message, Category, correlationId);

    /// <inheritdoc />
    public bool Equals(Error? other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(Code, other.Code, StringComparison.Ordinal)
            && string.Equals(Message, other.Message, StringComparison.Ordinal)
            && Category == other.Category
            && string.Equals(CorrelationId, other.CorrelationId, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Error);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = StringComparer.Ordinal.GetHashCode(Code);
            hash = (hash * 397) ^ (int)Category;
            return hash;
        }
    }

    /// <summary>Formats as <c>CODE: message</c> for diagnostics. Safe to log.</summary>
    public override string ToString() => Code + ": " + Message;
}
