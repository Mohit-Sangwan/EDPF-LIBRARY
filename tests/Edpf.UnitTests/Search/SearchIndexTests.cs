using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Search;
using Edpf.Search;

namespace Edpf.UnitTests.Search;

/// <summary>
/// The search platform. Almost every test here is about the aggregation
/// side-channel: search leaks through totals and facet counts long before it
/// leaks a document.
/// </summary>
public sealed class SearchIndexTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly InMemorySearchIndex<Record> _index = new("records", new RecordMap());

    private sealed record Record(string Text, string? Scope, string Ward);

    private sealed class RecordMap : ISearchDocumentMap<Record>
    {
        public string TextOf(Record document) => document.Text;

        public string? RequiredScopeOf(Record document) => document.Scope;

        public IReadOnlyDictionary<string, string> FacetsOf(Record document)
            => new Dictionary<string, string>(StringComparer.Ordinal) { ["ward"] = document.Ward };
    }

    private static SearchQuery Query(
        Guid tenantId,
        string text = "",
        int pageSize = 20,
        int pageNumber = 1,
        params string[] scopes)
        => new(tenantId, text, new PageRequest(pageNumber, pageSize), ["ward"], scopes);

    // ── tenancy, including the aggregation channel ────────────────────────

    [Fact]
    public async Task Search_NeverReturnsAnotherTenantsDocuments()
    {
        await _index.IndexAsync(TenantA, "a-1", new Record("shared term", null, "w1"), default);
        await _index.IndexAsync(TenantB, "b-1", new Record("shared term", null, "w2"), default);

        SearchResults<Record> results = (await _index.SearchAsync(Query(TenantB, "shared"), default)).Value;

        Assert.Equal("w2", Assert.Single(results.Documents).Ward);
    }

    [Fact]
    public async Task TotalHits_CountsOnlyWhatTheCallerMaySee()
    {
        // An untrimmed total tells one tenant how many records another holds.
        // That is a leak even though no document crossed.
        await _index.IndexAsync(TenantA, "a-1", new Record("term", null, "w1"), default);
        await _index.IndexAsync(TenantA, "a-2", new Record("term", null, "w1"), default);
        await _index.IndexAsync(TenantB, "b-1", new Record("term", null, "w2"), default);

        SearchResults<Record> results = (await _index.SearchAsync(Query(TenantB, "term"), default)).Value;

        Assert.Equal(1, results.TotalHits);
    }

    [Fact]
    public async Task FacetCounts_AreComputedOverInScopeDocumentsOnly()
    {
        // The distribution is as disclosing as the count. A facet showing
        // "oncology: 47" tells the caller something about records they cannot
        // open.
        await _index.IndexAsync(TenantA, "a-1", new Record("term", null, "oncology"), default);
        await _index.IndexAsync(TenantB, "b-1", new Record("term", null, "general"), default);

        SearchResults<Record> results = (await _index.SearchAsync(Query(TenantB, "term"), default)).Value;

        Assert.False(results.FacetCounts["ward"].ContainsKey("oncology"));
        Assert.Equal(1, results.FacetCounts["ward"]["general"]);
    }

    [Fact]
    public async Task IndexAsync_WithoutATenant_IsRefused()
    {
        Result indexed = await _index.IndexAsync(
            Guid.Empty, "x", new Record("term", null, "w"), default);

        Assert.True(indexed.IsFailure);
    }

    [Fact]
    public async Task TwoTenantsMayUseTheSameDocumentId()
    {
        // "patient-1" is a natural key both will pick. Keying by id alone
        // would have one tenant's write silently replace the other's.
        await _index.IndexAsync(TenantA, "patient-1", new Record("alpha", null, "w1"), default);
        await _index.IndexAsync(TenantB, "patient-1", new Record("beta", null, "w2"), default);

        Assert.Equal(2, _index.Count);
        Assert.Equal("alpha", Assert.Single(
            (await _index.SearchAsync(Query(TenantA, "alpha"), default)).Value.Documents).Text);
    }

    // ── trimming happens at query time ────────────────────────────────────

    [Fact]
    public async Task Search_HidesADocumentTheCallerLacksTheScopeFor()
    {
        await _index.IndexAsync(TenantA, "open", new Record("term", null, "w1"), default);
        await _index.IndexAsync(TenantA, "restricted", new Record("term", "clinical.read", "w1"), default);

        SearchResults<Record> results = (await _index.SearchAsync(Query(TenantA, "term"), default)).Value;

        Assert.Equal(1, results.TotalHits);
    }

    [Fact]
    public async Task GrantingAScope_RevealsTheDocumentWithoutReindexing()
    {
        // The reason trimming is at query time. Index-time trimming bakes in
        // the permissions that existed when the document was written, and a
        // clinician who gains a scope on Monday should not wait for a reindex.
        await _index.IndexAsync(TenantA, "restricted", new Record("term", "clinical.read", "w1"), default);

        SearchResults<Record> before = (await _index.SearchAsync(Query(TenantA, "term"), default)).Value;
        SearchResults<Record> after = (await _index.SearchAsync(
            Query(TenantA, "term", scopes: "clinical.read"), default)).Value;

        Assert.Equal(0, before.TotalHits);
        Assert.Equal(1, after.TotalHits);
    }

    // ── ranking and paging ────────────────────────────────────────────────

    [Fact]
    public async Task Search_RanksMoreOccurrencesHigher()
    {
        await _index.IndexAsync(TenantA, "weak", new Record("fracture once", null, "w1"), default);
        await _index.IndexAsync(TenantA, "strong", new Record("fracture fracture fracture", null, "w1"), default);

        SearchResults<Record> results = (await _index.SearchAsync(Query(TenantA, "fracture"), default)).Value;

        Assert.Equal("fracture fracture fracture", results.Documents[0].Text);
    }

    [Fact]
    public async Task Search_OrdersStablyWhenScoresTie()
    {
        // Without the tiebreak, two equally-scored documents can swap between
        // requests and paging then repeats or skips results — a defect that
        // only ever shows up on page two.
        for (int i = 0; i < 5; i++)
        {
            await _index.IndexAsync(
                TenantA, "doc-" + i, new Record("identical", null, "w1"), default);
        }

        SearchResults<Record> first = (await _index.SearchAsync(Query(TenantA, "identical"), default)).Value;
        SearchResults<Record> second = (await _index.SearchAsync(Query(TenantA, "identical"), default)).Value;

        Assert.Equal(first.Documents, second.Documents);
    }

    [Fact]
    public async Task Search_PagesWithoutRepeatingOrSkipping()
    {
        // Distinct text per document. Record is a value type for equality, so
        // five identical records would compare equal and Intersect would report
        // an overlap the paging never produced — the assertion being wrong, not
        // the index.
        for (int i = 0; i < 5; i++)
        {
            await _index.IndexAsync(
                TenantA, "doc-" + i, new Record("term note-" + i, null, "w1"), default);
        }

        SearchResults<Record> page1 = (await _index.SearchAsync(
            Query(TenantA, "term", pageSize: 2, pageNumber: 1), default)).Value;
        SearchResults<Record> page2 = (await _index.SearchAsync(
            Query(TenantA, "term", pageSize: 2, pageNumber: 2), default)).Value;

        Assert.Equal(5, page1.TotalHits);
        Assert.Equal(2, page1.Documents.Count);
        Assert.Equal(2, page2.Documents.Count);
        Assert.Empty(page1.Documents.Intersect(page2.Documents));
    }

    [Fact]
    public async Task Search_WithNoTerms_BrowsesRatherThanMatchingNothing()
    {
        // A search box that returns zero results before you type is a search
        // box people stop using.
        await _index.IndexAsync(TenantA, "a-1", new Record("anything", null, "w1"), default);

        SearchResults<Record> results = (await _index.SearchAsync(Query(TenantA), default)).Value;

        Assert.Equal(1, results.TotalHits);
    }

    // ── there is no query syntax to inject into ───────────────────────────

    [Theory]
    [InlineData("ward:oncology OR *")]
    [InlineData("{\"match_all\":{}}")]
    [InlineData("term AND NOT scope")]
    public async Task Search_TreatsQuerySyntaxAsOrdinaryWords(string hostile)
    {
        // The contract promises the text is "a bound term, never concatenated".
        // That is true here for a structural reason: there is no query syntax,
        // so there is nothing to escape into.
        await _index.IndexAsync(TenantA, "a-1", new Record("a routine note", null, "w1"), default);

        SearchResults<Record> results = (await _index.SearchAsync(Query(TenantA, hostile), default)).Value;

        Assert.Equal(0, results.TotalHits);
    }

    [Fact]
    public async Task Search_IsCaseInsensitiveAndIgnoresPunctuation()
    {
        await _index.IndexAsync(TenantA, "a-1", new Record("Left-sided FRACTURE.", null, "w1"), default);

        Assert.Equal(1, (await _index.SearchAsync(Query(TenantA, "fracture"), default)).Value.TotalHits);
        Assert.Equal(1, (await _index.SearchAsync(Query(TenantA, "left"), default)).Value.TotalHits);
    }

    [Fact]
    public async Task Remove_OfAnotherTenantsDocument_IsNotFound()
    {
        await _index.IndexAsync(TenantA, "a-1", new Record("term", null, "w1"), default);

        Assert.True(_index.Remove(TenantB, "a-1").IsFailure);
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public void Query_CannotBeConstructedWithoutATenant()
        => Assert.Throws<ArgumentException>(
            () => new SearchQuery(Guid.Empty, "x", new PageRequest(1, 10)));
}
