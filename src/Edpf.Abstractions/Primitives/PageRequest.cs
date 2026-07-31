using System;

namespace Edpf.Abstractions.Primitives;

/// <summary>
/// The one pagination request contract for the whole framework (Phase 01).
/// Page numbers are 1-based. Page size is bounded: unbounded reads are a
/// memory-exhaustion vector and are rejected at construction, not at the store.
/// </summary>
public readonly struct PageRequest : IEquatable<PageRequest>
{
    /// <summary>The default page size when none is specified.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The maximum permitted page size. Larger reads must stream (Phase 08).</summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// Initializes a page request.
    /// </summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="pageSize">Items per page, 1..<see cref="MaxPageSize"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pageNumber"/> &lt; 1, or <paramref name="pageSize"/> outside 1..<see cref="MaxPageSize"/>.
    /// </exception>
    public PageRequest(int pageNumber, int pageSize = DefaultPageSize)
    {
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, "Page number is 1-based.");
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize), pageSize, "Page size must be between 1 and " + MaxPageSize + ".");
        }

        _pageNumber = pageNumber;
        _pageSize = pageSize;
    }

    /// <summary>1-based page number. The default instance is page 1.</summary>
    public int PageNumber => _pageNumber == 0 ? 1 : _pageNumber;

    /// <summary>Items per page. The default instance uses <see cref="DefaultPageSize"/>.</summary>
    public int PageSize => _pageSize == 0 ? DefaultPageSize : _pageSize;

    private readonly int _pageNumber;
    private readonly int _pageSize;

    /// <summary>Number of items to skip for offset pagination.</summary>
    public int Skip => (PageNumber - 1) * PageSize;

    /// <summary>The first page at the default size.</summary>
    public static PageRequest First => default;

    /// <inheritdoc />
    public bool Equals(PageRequest other)
        => PageNumber == other.PageNumber && PageSize == other.PageSize;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PageRequest other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            return (PageNumber * 397) ^ PageSize;
        }
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(PageRequest left, PageRequest right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(PageRequest left, PageRequest right) => !left.Equals(right);

    /// <summary>Formats as <c>page N (size S)</c>. Safe to log.</summary>
    public override string ToString() => "page " + PageNumber + " (size " + PageSize + ")";
}
