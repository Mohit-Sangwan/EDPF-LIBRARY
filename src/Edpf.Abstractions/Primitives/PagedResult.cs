using System;
using System.Collections.Generic;

namespace Edpf.Abstractions.Primitives;

/// <summary>
/// The one pagination result contract for the whole framework (Phase 01).
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>
    /// Initializes a page of results.
    /// </summary>
    /// <param name="items">The items on this page. Not null; may be empty.</param>
    /// <param name="request">The request that produced this page.</param>
    /// <param name="totalCount">Total items across all pages. Must be ≥ 0.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="totalCount"/> is negative.</exception>
    public PagedResult(IReadOnlyList<T> items, PageRequest request, long totalCount)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));

        if (totalCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCount), totalCount, "Total count cannot be negative.");
        }

        PageNumber = request.PageNumber;
        PageSize = request.PageSize;
        TotalCount = totalCount;
    }

    /// <summary>The items on this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>1-based page number.</summary>
    public int PageNumber { get; }

    /// <summary>Requested items per page.</summary>
    public int PageSize { get; }

    /// <summary>Total items across all pages.</summary>
    public long TotalCount { get; }

    /// <summary>Total number of pages.</summary>
    public long TotalPages => TotalCount == 0 ? 0 : ((TotalCount - 1) / PageSize) + 1;

    /// <summary>True when a later page exists.</summary>
    public bool HasNextPage => PageNumber < TotalPages;

    /// <summary>True when an earlier page exists.</summary>
    public bool HasPreviousPage => PageNumber > 1;
}
