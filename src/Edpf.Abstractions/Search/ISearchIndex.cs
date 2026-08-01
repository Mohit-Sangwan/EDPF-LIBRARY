using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Search;

/// <summary>
/// A search index (Phase 16). Corrects a categorisation error in the original
/// specification: Elasticsearch/OpenSearch is **a search index fed from a
/// system of record, never the system of record itself**.
/// </summary>
/// <typeparam name="TDocument">The indexed projection.</typeparam>
/// <remarks>
/// Indexes are populated from the Phase 09 outbox, so consistency is eventual
/// with a measured, alertable lag. Nothing may be read from an index that
/// cannot be reconstructed from the store.
/// </remarks>
public interface ISearchIndex<TDocument>
    where TDocument : class
{
    /// <summary>The index name, aliased so a reindex can swap without downtime.</summary>
    string IndexName { get; }

    /// <summary>
    /// Executes a query, applying security trimming.
    /// </summary>
    /// <param name="query">The query, carrying the caller's scope.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Results the caller is entitled to see, and nothing else.</returns>
    Task<Result<SearchResults<TDocument>>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Indexes or replaces a document.
    /// </summary>
    /// <param name="tenantId">The owning tenant; stored on the document and enforced at query time.</param>
    /// <param name="documentId">Document identifier, unique within the tenant.</param>
    /// <param name="document">The projection to index.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success once indexed.</returns>
    Task<Result> IndexAsync(
        Guid tenantId, string documentId, TDocument document, CancellationToken cancellationToken);
}

/// <summary>
/// A search request. The tenant is a **required constructor argument**, so a
/// query that is not tenant-scoped cannot be expressed.
/// </summary>
public sealed class SearchQuery
{
    /// <summary>
    /// Initializes a query.
    /// </summary>
    /// <param name="tenantId">The caller's tenant. Must not be empty.</param>
    /// <param name="text">Free-text terms. Passed to the engine as a bound term, never concatenated.</param>
    /// <param name="page">Which page of results.</param>
    /// <param name="facets">Facet fields to aggregate.</param>
    /// <param name="authorizationScopes">
    /// Field-level scopes the caller holds, applied at query time.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is empty.</exception>
    public SearchQuery(
        Guid tenantId,
        string text,
        PageRequest page,
        IReadOnlyList<string>? facets = null,
        IReadOnlyList<string>? authorizationScopes = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A search query requires a tenant; an unscoped search is not expressible.", nameof(tenantId));
        }

        TenantId = tenantId;
        Text = text ?? string.Empty;
        Page = page;
        Facets = facets ?? [];
        AuthorizationScopes = authorizationScopes ?? [];
    }

    /// <summary>The caller's tenant.</summary>
    public Guid TenantId { get; }

    /// <summary>Free-text terms.</summary>
    public string Text { get; }

    /// <summary>Which page of results.</summary>
    public PageRequest Page { get; }

    /// <summary>Facet fields to aggregate.</summary>
    public IReadOnlyList<string> Facets { get; }

    /// <summary>
    /// Field-level scopes the caller holds. Applied **at query time**, not
    /// merely at index time — index-time trimming bakes in the permissions
    /// that existed when the document was written, and permissions change.
    /// </summary>
    public IReadOnlyList<string> AuthorizationScopes { get; }
}

/// <summary>Search results, already trimmed.</summary>
/// <typeparam name="TDocument">The indexed projection.</typeparam>
public sealed class SearchResults<TDocument>
    where TDocument : class
{
    /// <summary>
    /// Initializes results.
    /// </summary>
    /// <param name="documents">Matching documents the caller may see.</param>
    /// <param name="totalHits">Total matches within the caller's scope.</param>
    /// <param name="facetCounts">Facet counts over in-scope documents only.</param>
    public SearchResults(
        IReadOnlyList<TDocument> documents,
        long totalHits,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> facetCounts)
    {
        Documents = documents ?? throw new ArgumentNullException(nameof(documents));
        TotalHits = totalHits;
        FacetCounts = facetCounts ?? throw new ArgumentNullException(nameof(facetCounts));
    }

    /// <summary>Matching documents.</summary>
    public IReadOnlyList<TDocument> Documents { get; }

    /// <summary>
    /// Total matches within the caller's scope.
    /// </summary>
    /// <remarks>
    /// Counted after trimming, never before. An untrimmed total is a leak: it
    /// tells one tenant how many records another holds, which is exactly the
    /// aggregation side-channel that makes search a classic cross-tenant
    /// route (Phase 16 verification).
    /// </remarks>
    public long TotalHits { get; }

    /// <summary>
    /// Facet counts, computed only over in-scope documents — for the same
    /// reason <see cref="TotalHits"/> is.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> FacetCounts { get; }
}
