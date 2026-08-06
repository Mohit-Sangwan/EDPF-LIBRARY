using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Search;
using Edpf.Core.Guards;

namespace Edpf.Search;

/// <summary>
/// Tells the index what part of a document is searchable, who may see it, and
/// what it can be faceted by.
/// </summary>
/// <typeparam name="TDocument">The indexed projection.</typeparam>
/// <remarks>
/// A map rather than reflection or attributes, for the ADR-025 reason: a
/// projection assembled at runtime from customer-defined fields has no CLR
/// members to reflect over, and the moment searchability depends on reflection
/// those documents fall outside the model.
/// </remarks>
public interface ISearchDocumentMap<TDocument>
    where TDocument : class
{
    /// <summary>The text to index.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Everything that should match a free-text query.</returns>
    string TextOf(TDocument document);

    /// <summary>
    /// The authorization scope required to see this document, or null when
    /// anyone in the tenant may.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The required scope, or null.</returns>
    string? RequiredScopeOf(TDocument document);

    /// <summary>Facet field values, keyed by facet name.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The facet values.</returns>
    IReadOnlyDictionary<string, string> FacetsOf(TDocument document);
}

/// <summary>
/// An in-memory inverted index with tenant scoping and query-time security
/// trimming.
/// </summary>
/// <typeparam name="TDocument">The indexed projection.</typeparam>
/// <remarks>
/// <para>
/// **Trimming happens at query time, not index time**, and the contract this
/// implements is emphatic about it. Index-time trimming bakes in the
/// permissions that existed when the document was written; permissions change,
/// and a clinician who gains a scope on Monday should not have to wait for a
/// reindex to find the record. There is a test that grants a scope and
/// re-queries without touching the index.
/// </para>
/// <para>
/// **Totals and facets are computed after trimming.** An untrimmed total tells
/// one tenant how many records another holds, and an untrimmed facet count
/// tells them the distribution — which is the aggregation side-channel that
/// makes search a classic cross-tenant route even when the documents
/// themselves never leak.
/// </para>
/// <para>
/// Suitable up to hundreds of thousands of documents per process. Past that a
/// deployment swaps in Elasticsearch or OpenSearch behind
/// <see cref="ISearchIndex{TDocument}"/> — and inherits the trimming, because
/// it lives here rather than in the engine.
/// </para>
/// </remarks>
public sealed class InMemorySearchIndex<TDocument> : ISearchIndex<TDocument>
    where TDocument : class
{
    private readonly ISearchDocumentMap<TDocument> _map;
    private readonly Dictionary<string, Entry> _documents = new(StringComparer.Ordinal);

    /// <summary>
    /// Composes the index.
    /// </summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="map">How to read a document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="indexName"/> is blank.</exception>
    public InMemorySearchIndex(string indexName, ISearchDocumentMap<TDocument> map)
    {
        IndexName = Guard.NotNullOrWhiteSpace(indexName, nameof(indexName));
        _map = Guard.NotNull(map, nameof(map));
    }

    /// <inheritdoc />
    public string IndexName { get; }

    /// <summary>How many documents are indexed, across all tenants.</summary>
    public int Count => _documents.Count;

    /// <inheritdoc />
    public Task<Result> IndexAsync(
        Guid tenantId,
        string documentId,
        TDocument document,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(documentId, nameof(documentId));
        Guard.NotNull(document, nameof(document));
        cancellationToken.ThrowIfCancellationRequested();

        if (tenantId == Guid.Empty)
        {
            return Task.FromResult(Result.Failure(new Error(
                ErrorCodes.TenantScopeViolation,
                "An indexed document requires a tenant.",
                ErrorCategory.NotFound)));
        }

        // Keyed by tenant AND id, so two tenants using the same natural key —
        // "patient-1" — do not overwrite each other.
        string key = tenantId.ToString("D") + "|" + documentId;

        _documents[key] = new Entry(
            tenantId,
            documentId,
            document,
            Tokenize(_map.TextOf(document)),
            _map.RequiredScopeOf(document),
            _map.FacetsOf(document));

        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result<SearchResults<TDocument>>> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(query, nameof(query));
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> terms = Tokenize(query.Text);

        // Pass one: everything in this tenant that the caller may see. The two
        // filters are applied together and before anything is counted.
        var visible = new List<Entry>();

        foreach (Entry entry in _documents.Values)
        {
            if (entry.TenantId != query.TenantId)
            {
                continue;
            }

            if (entry.RequiredScope is not null && !Holds(query.AuthorizationScopes, entry.RequiredScope))
            {
                continue;
            }

            visible.Add(entry);
        }

        // Pass two: score. An empty query browses rather than matching nothing
        // — a search box that returns zero results before you type is a search
        // box people stop using.
        var scored = new List<(Entry Entry, double Score)>();

        foreach (Entry entry in visible)
        {
            if (terms.Count == 0)
            {
                scored.Add((entry, 0));
                continue;
            }

            double score = Score(entry, terms, visible.Count);
            if (score > 0)
            {
                scored.Add((entry, score));
            }
        }

        // Ordering is stable: score descending, then document id. Without the
        // tiebreak, two documents with equal scores can swap between requests
        // and paging then repeats or skips results — a defect that only ever
        // shows up on page two.
        scored.Sort((left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0
                ? byScore
                : string.CompareOrdinal(left.Entry.DocumentId, right.Entry.DocumentId);
        });

        var facetCounts = CountFacets(scored, query.Facets);

        int skip = Math.Max(0, (query.Page.PageNumber - 1) * query.Page.PageSize);
        var page = new List<TDocument>();

        for (int i = skip; i < scored.Count && page.Count < query.Page.PageSize; i++)
        {
            page.Add(scored[i].Entry.Document);
        }

        return Task.FromResult(Result<SearchResults<TDocument>>.FromValue(
            new SearchResults<TDocument>(page, scored.Count, facetCounts)));
    }

    /// <summary>Removes a document.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="documentId">The document id.</param>
    /// <returns>Success, or not-found — including for another tenant's document.</returns>
    public Result Remove(Guid tenantId, string documentId)
    {
        Guard.NotNullOrWhiteSpace(documentId, nameof(documentId));

        string key = tenantId.ToString("D") + "|" + documentId;

        return _documents.Remove(key)
            ? Result.Success()
            : Result.Failure(new Error(
                ErrorCodes.NotFound,
                "The requested resource was not found.",
                ErrorCategory.NotFound));
    }

    /// <summary>
    /// Term frequency weighted by inverse document frequency.
    /// </summary>
    /// <remarks>
    /// Computed over the **visible** set rather than the whole index. Using the
    /// global document count would make a term's rarity depend on documents the
    /// caller cannot see — a small leak, but a real one, and it would vary the
    /// ranking depending on what another tenant happened to index.
    /// </remarks>
    private static double Score(Entry entry, IReadOnlyList<string> terms, int visibleCount)
    {
        double score = 0;

        foreach (string term in terms)
        {
            int occurrences = 0;
            foreach (string token in entry.Tokens)
            {
                if (string.Equals(token, term, StringComparison.Ordinal))
                {
                    occurrences++;
                }
            }

            if (occurrences > 0)
            {
                score += occurrences * Math.Log(1 + ((double)visibleCount / occurrences));
            }
        }

        return score;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, long>> CountFacets(
        List<(Entry Entry, double Score)> matches,
        IReadOnlyList<string> facets)
    {
        var counts = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);

        foreach (string facet in facets)
        {
            var values = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach ((Entry entry, double _) in matches)
            {
                if (entry.Facets.TryGetValue(facet, out string? value))
                {
                    values[value] = values.TryGetValue(value, out long existing) ? existing + 1 : 1;
                }
            }

            counts[facet] = values;
        }

        return counts;
    }

    private static bool Holds(IReadOnlyList<string> held, string required)
    {
        for (int i = 0; i < held.Count; i++)
        {
            if (string.Equals(held[i], required, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits text into lowercase alphanumeric terms.
    /// </summary>
    /// <remarks>
    /// Query text becomes terms by exactly this function, which is what makes
    /// the "bound term, never concatenated" promise in the contract true here:
    /// there is no query syntax to inject into, because there is no query
    /// syntax. A caller typing <c>field:value OR *</c> searches for those
    /// words.
    /// </remarks>
    private static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private sealed class Entry(
        Guid tenantId,
        string documentId,
        TDocument document,
        IReadOnlyList<string> tokens,
        string? requiredScope,
        IReadOnlyDictionary<string, string> facets)
    {
        internal Guid TenantId { get; } = tenantId;

        internal string DocumentId { get; } = documentId;

        internal TDocument Document { get; } = document;

        internal IReadOnlyList<string> Tokens { get; } = tokens;

        internal string? RequiredScope { get; } = requiredScope;

        internal IReadOnlyDictionary<string, string> Facets { get; } = facets;
    }
}
