using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Storage;

namespace Edpf.ConformanceTests;

/// <summary>
/// The identical test set every storage backend must pass (ADR-008, Z.12).
/// </summary>
/// <remarks>
/// <para>
/// A backend is not "supported" because someone wrote it and it seemed to
/// work. It is supported when it passes this suite, which is why the suite is
/// a base class rather than a document: adding a backend means adding four
/// lines and running, and a backend that skips the suite has to skip it
/// visibly.
/// </para>
/// <para>
/// Note what is **not** here. Tenancy, encryption, content-type coercion and
/// bounded reads are absent because backends do not implement them —
/// <see cref="TenantScopedBlobStore"/> does, once, above all of them. A
/// conformance suite that tested those per backend would be testing the same
/// code sixteen times and the backend zero times.
/// </para>
/// </remarks>
public abstract class BlobBackendConformance
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    /// <summary>Creates the backend under test.</summary>
    protected abstract IBlobBackend CreateBackend();

    [Fact]
    public void BackendName_IsDeclared()
    {
        // Diagnostics name the backend that failed. An unnamed one produces
        // "storage error" in an incident channel at 3 a.m.
        Assert.False(string.IsNullOrWhiteSpace(CreateBackend().BackendName));
    }

    [Fact]
    public async Task Put_ThenGet_ReturnsTheSameBytes()
    {
        IBlobBackend backend = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "reports", "q3.bin");
        byte[] payload = [0x00, 0x01, 0xFE, 0xFF, 0x7F];

        Assert.True((await backend.PutAsync(path, payload, default)).IsSuccess);

        Result<byte[]> read = await backend.GetAsync(path, default);

        Assert.True(read.IsSuccess);
        Assert.Equal(payload, read.Value);
    }

    [Fact]
    public async Task Put_OverAnExistingBlob_Replaces()
    {
        IBlobBackend backend = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "reports", "q3.bin");

        await backend.PutAsync(path, [1, 2, 3], default);
        await backend.PutAsync(path, [9], default);

        Result<byte[]> read = await backend.GetAsync(path, default);

        Assert.Equal([9], read.Value);
    }

    [Fact]
    public async Task Get_OfAnAbsentPath_IsNotFound()
    {
        IBlobBackend backend = CreateBackend();

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "nothing"), default);

        Assert.True(read.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, read.Error!.Code);
    }

    [Fact]
    public async Task Remove_OfAnAbsentPath_IsNotFound()
    {
        IBlobBackend backend = CreateBackend();

        Result removed = await backend.RemoveAsync(BlobPath.Create(TenantA, "nothing"), default);

        Assert.True(removed.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, removed.Error!.Code);
    }

    [Fact]
    public async Task Remove_TakesTheMetadataWithIt()
    {
        // A surviving sidecar would describe a blob that no longer exists, and
        // the next write to that path would inherit somebody else's
        // classification.
        IBlobBackend backend = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "reports", "q3.bin");

        await backend.PutAsync(path, [1], default);
        await backend.PutMetadataAsync(path, new Dictionary<string, string> { ["k"] = "v" }, default);

        await backend.RemoveAsync(path, default);

        Assert.True((await backend.GetMetadataAsync(path, default)).IsFailure);
    }

    [Fact]
    public async Task Metadata_RoundTripsVerbatim_IncludingHostileValues()
    {
        // One of these values is the caller's declared content type, which is
        // arbitrary text from outside. A backend storing metadata in a
        // line-oriented or delimiter-separated format corrupts on the first
        // newline — and the corruption lands on the field that records what
        // classification the blob is.
        IBlobBackend backend = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "reports", "q3.bin");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["edpf.declared-content-type"] = "text/html\r\nedpf.classification=Public",
            ["edpf.classification"] = "Phi",
            ["quoted"] = "he said \"hello\" and left",
            ["unicode"] = "Ünïcødé — ✓",
            ["empty"] = string.Empty,
        };

        await backend.PutAsync(path, [1], default);
        Assert.True((await backend.PutMetadataAsync(path, metadata, default)).IsSuccess);

        Result<IReadOnlyDictionary<string, string>> read = await backend.GetMetadataAsync(path, default);

        Assert.True(read.IsSuccess);
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            Assert.Equal(entry.Value, read.Value[entry.Key]);
        }
    }

    [Fact]
    public async Task GetMetadata_OfAnAbsentPath_IsNotFound()
    {
        IBlobBackend backend = CreateBackend();

        Result<IReadOnlyDictionary<string, string>> read =
            await backend.GetMetadataAsync(BlobPath.Create(TenantA, "nothing"), default);

        Assert.True(read.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, read.Error!.Code);
    }

    [Fact]
    public async Task List_ReturnsPathsUnderThePrefixAndNothingElse()
    {
        IBlobBackend backend = CreateBackend();
        BlobPath inside = BlobPath.Create(TenantA, "reports", "q3.bin");
        BlobPath outside = BlobPath.Create(TenantA, "invoices", "q3.bin");

        await backend.PutAsync(inside, [1], default);
        await backend.PutAsync(outside, [1], default);

        Result<IReadOnlyList<string>> listed =
            await backend.ListAsync("tenants/" + TenantA.ToString("D") + "/reports/", default);

        Assert.True(listed.IsSuccess);
        Assert.Contains(inside.Value, listed.Value);
        Assert.DoesNotContain(outside.Value, listed.Value);
    }

    [Fact]
    public async Task List_OfAnEmptyPrefix_IsEmptyRatherThanAFailure()
    {
        // An empty folder is a normal state, not an error. A backend that
        // failed here would make "no documents yet" indistinguishable from
        // "storage is down".
        IBlobBackend backend = CreateBackend();

        Result<IReadOnlyList<string>> listed =
            await backend.ListAsync("tenants/" + TenantA.ToString("D") + "/nothing/", default);

        Assert.True(listed.IsSuccess);
        Assert.Empty(listed.Value);
    }
}

/// <summary>The in-memory backend under the standard suite.</summary>
public sealed class InMemoryBlobBackendConformanceTests : BlobBackendConformance
{
    private readonly InMemoryBlobBackend _backend = new();

    /// <inheritdoc />
    protected override IBlobBackend CreateBackend() => _backend;
}

/// <summary>
/// The filesystem backend under the same suite, against a real directory.
/// </summary>
/// <remarks>
/// This suite touches disk, which is why it lives here rather than in the unit
/// tests (Z.7 forbids I/O there). It is also the only reason the filesystem
/// backend is a tested capability rather than a claimed one — the distinction
/// this programme has already been caught on once.
/// </remarks>
public sealed class FileSystemBlobBackendConformanceTests : BlobBackendConformance, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "edpf-storage-conformance", Guid.NewGuid().ToString("N"));

    private readonly FileSystemBlobBackend _backend;

    /// <summary>Roots the backend in a directory unique to this test class instance.</summary>
    public FileSystemBlobBackendConformanceTests() => _backend = new FileSystemBlobBackend(_root);

    /// <inheritdoc />
    protected override IBlobBackend CreateBackend() => _backend;

    /// <summary>Removes the temporary directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
