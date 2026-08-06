using System.Text;
using Edpf.Abstractions.Compliance;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.Metadata;
using Edpf.Storage;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Storage;

/// <summary>
/// The capabilities under the storage head beyond upload and download:
/// scanning, compression, versioning, retention, chunked upload and streaming.
/// </summary>
public sealed class StorageCapabilityTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Subject = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private readonly InMemoryBlobBackend _backend = new();
    private readonly TenantContextAccessor _tenants = new();
    private readonly ReversibleTestCryptoProvider _crypto = new();
    private readonly FakeClock _clock = new();

    private TenantScopedBlobStore CreateStore(
        IContentScanner? scanner = null,
        IContentExtractor? extractor = null,
        IStorageAuditSink? audit = null)
        => new(_backend, _tenants, ProtectionPolicy.Default, new TestHashingService(),
            _clock, _crypto, scanner, extractor, audit);

    private IDisposable ActAs(Guid tenantId)
        => _tenants.Push(new TenantDescriptor(
            tenantId, "tenant", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()));

    private static MemoryStream Payload(string text) => new(Encoding.UTF8.GetBytes(text));

    private static BlobWriteOptions Options(
        bool compress = false,
        DateTimeOffset? retainUntil = null,
        Guid? subjectId = null,
        DataClassificationLevel classification = DataClassificationLevel.Public)
        => new(classification, "text/plain", 1_000_000, subjectId, compress, retainUntil);

    // ── virus scanning, fail closed ───────────────────────────────────────

    [Fact]
    public async Task WriteAsync_OfInfectedContent_IsRefusedAndStoresNothing()
    {
        TenantScopedBlobStore store = CreateStore(new StubScanner(ScanVerdict.Infected));
        BlobPath path = BlobPath.Create(TenantA, "upload.txt");

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(path, Payload("eicar"), Options(), default);

            Assert.True(written.IsFailure);
        }

        Assert.Null(_backend.RawBytesAt(path.Value));
    }

    [Fact]
    public async Task WriteAsync_OfIndeterminateContent_IsAlsoRefused()
    {
        // "I could not tell" is not a soft clean. A password-protected archive
        // that the engine cannot open is the standard way this control gets
        // walked past.
        TenantScopedBlobStore store = CreateStore(new StubScanner(ScanVerdict.Indeterminate));

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                BlobPath.Create(TenantA, "archive.zip"), Payload("encrypted archive"), Options(), default);

            Assert.True(written.IsFailure);
        }
    }

    [Fact]
    public async Task WriteAsync_WhenTheScannerItselfFails_IsRefused()
    {
        // A scanner that errored has not cleared the content, and an outage in
        // the scanning service must not become an open door.
        TenantScopedBlobStore store = CreateStore(new FailingScanner());

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                BlobPath.Create(TenantA, "upload.txt"), Payload("content"), Options(), default);

            Assert.True(written.IsFailure);
        }
    }

    [Fact]
    public async Task WriteAsync_WithNoScannerConfigured_RecordsNotScanned()
    {
        // Honest rather than optimistic. The descriptor says what is known, and
        // "nobody looked" is a different fact from "it is clean".
        TenantScopedBlobStore store = CreateStore();

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                BlobPath.Create(TenantA, "upload.txt"), Payload("content"), Options(), default);

            Assert.Equal(ScanState.NotScanned, written.Value.ScanState);
        }
    }

    [Fact]
    public async Task WriteAsync_ScansThePlaintext_NotTheCiphertext()
    {
        // Scanning ciphertext finds nothing, every time. The order has to be
        // scan-then-encrypt, and this asserts the scanner saw readable bytes.
        var scanner = new StubScanner(ScanVerdict.Clean);
        TenantScopedBlobStore store = CreateStore(scanner);

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                BlobPath.Create(TenantA, "chart.txt"),
                Payload("MRN-000123"),
                Options(classification: DataClassificationLevel.Phi),
                default);
        }

        Assert.Equal("MRN-000123", Encoding.UTF8.GetString(scanner.LastScanned!));
    }

    // ── compression ───────────────────────────────────────────────────────

    [Fact]
    public async Task CompressedBlob_RoundTripsToTheOriginalBytes()
    {
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "notes.txt");
        string original = new string('a', 5000);

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                path, Payload(original), Options(compress: true), default);

            Assert.True(written.Value.IsCompressed);
            Assert.Equal(5000, written.Value.Length);

            using BlobContent content = (await store.ReadAsync(path, default)).Value;
            using var reader = new StreamReader(content.Content);

            Assert.Equal(original, await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task CompressedBlob_ActuallyOccupiesLessSpace()
    {
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "notes.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload(new string('a', 5000)), Options(compress: true), default);
        }

        Assert.True(_backend.RawBytesAt(path.Value)!.Length < 5000);
    }

    [Fact]
    public async Task CompressedAndEncryptedBlob_RoundTrips()
    {
        // Compress then encrypt, and unwind in the mirror order. Getting this
        // pair backwards produces a blob that decrypts to compressed rubbish.
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                path,
                Payload("MRN-000123 repeated repeated repeated"),
                Options(compress: true, classification: DataClassificationLevel.Phi),
                default);

            using BlobContent content = (await store.ReadAsync(path, default)).Value;
            using var reader = new StreamReader(content.Content);

            Assert.Equal("MRN-000123 repeated repeated repeated", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public void Decompress_RefusesAPayloadThatExpandsPastItsBound()
    {
        // The decompression-bomb defence: a few kilobytes of crafted gzip
        // expands to gigabytes, and an unbounded buffer hands an uploader the
        // ability to exhaust the process at will.
        byte[] bomb = BlobCompression.Compress(new byte[200_000]);

        Result<byte[]> expanded = BlobCompression.Decompress(bomb, maxLength: 1024);

        Assert.True(expanded.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, expanded.Error!.Code);
    }

    // ── versioning ────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_OverAnExistingBlob_PreservesThePreviousVersion()
    {
        // Overwriting a clinical document in place destroys the version a
        // clinician signed, and "we have a backup" is not the same as being
        // able to produce the exact bytes that were signed.
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "summary.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("first draft"), Options(), default);
            Result<BlobDescriptor> second = await store.WriteAsync(path, Payload("final"), Options(), default);

            Assert.Equal(2, second.Value.Version);

            IReadOnlyList<BlobDescriptor> versions = (await store.ListVersionsAsync(path, default)).Value;
            BlobDescriptor archived = Assert.Single(versions);

            using BlobContent old = (await store.ReadAsync(archived.Path, default)).Value;
            using var reader = new StreamReader(old.Content);

            Assert.Equal("first draft", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task ListAsync_DoesNotReturnArchivedVersions()
    {
        // Otherwise "the documents in this folder" is a number that grows every
        // time one is edited.
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "docs", "summary.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("v1"), Options(), default);
            await store.WriteAsync(path, Payload("v2"), Options(), default);

            IReadOnlyList<BlobDescriptor> listed = (await store.ListAsync(["docs"], default)).Value;

            Assert.Single(listed);
        }
    }

    [Fact]
    public async Task WriteAsync_ToAPathEndingInTheReservedSuffix_IsRefused()
    {
        // Reserved so a caller-chosen name can never collide with an archived
        // version and overwrite another blob's history.
        TenantScopedBlobStore store = CreateStore();

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                BlobPath.Create(TenantA, "summary.txt__v1"), Payload("x"), Options(), default);

            Assert.True(written.IsFailure);
            Assert.Equal(ErrorCodes.ValidationFailed, written.Error!.Code);
        }
    }

    // ── retention and lifecycle ───────────────────────────────────────────

    [Fact]
    public async Task LifecycleSweep_DeletesOnlyWhatHasExpired()
    {
        TenantScopedBlobStore store = CreateStore();
        var sweep = new BlobLifecycleSweep(store, _clock);

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                BlobPath.Create(TenantA, "docs", "expired.txt"),
                Payload("old"),
                Options(retainUntil: _clock.UtcNow.AddDays(-1)),
                default);

            await store.WriteAsync(
                BlobPath.Create(TenantA, "docs", "current.txt"),
                Payload("new"),
                Options(retainUntil: _clock.UtcNow.AddYears(7)),
                default);

            LifecycleOutcome outcome = (await sweep.RunAsync(["docs"], default)).Value;

            Assert.Equal(1, outcome.Deleted);
            Assert.Equal(1, outcome.NotDue);
        }
    }

    [Fact]
    public async Task LifecycleSweep_NeverTouchesABlobWithNoDeclaredRetention()
    {
        // A period nobody chose should not start a clock.
        TenantScopedBlobStore store = CreateStore();
        var sweep = new BlobLifecycleSweep(store, _clock);
        BlobPath path = BlobPath.Create(TenantA, "docs", "forever.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("keep"), Options(), default);
            _clock.Advance(TimeSpan.FromDays(4000));

            LifecycleOutcome outcome = (await sweep.RunAsync(["docs"], default)).Value;

            Assert.Equal(0, outcome.Deleted);
            Assert.True((await store.StatAsync(path, default)).IsSuccess);
        }
    }

    [Fact]
    public async Task LifecycleSweep_RefusesToDeleteASubjectUnderLegalHold()
    {
        // A hold outranks a schedule. Deleting on schedule during litigation is
        // spoliation, and "the retention job did it" has never been a defence.
        TenantScopedBlobStore store = CreateStore();
        var sweep = new BlobLifecycleSweep(store, _clock, new HoldEverything(_clock));
        BlobPath path = BlobPath.Create(TenantA, "docs", "held.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                path,
                Payload("evidence"),
                Options(retainUntil: _clock.UtcNow.AddDays(-1), subjectId: Subject,
                    classification: DataClassificationLevel.Phi),
                default);

            LifecycleOutcome outcome = (await sweep.RunAsync(["docs"], default)).Value;

            Assert.Equal(0, outcome.Deleted);
            Assert.Equal(1, outcome.HeldBack);
            Assert.True((await store.StatAsync(path, default)).IsSuccess);
        }
    }

    [Fact]
    public async Task LifecycleSweep_WithNoHoldStore_SkipsSubjectBlobsRatherThanDeletingThem()
    {
        // A hold that cannot be checked is not a hold that is absent. Skipping
        // costs storage; deleting could cost a spoliation finding.
        TenantScopedBlobStore store = CreateStore();
        var sweep = new BlobLifecycleSweep(store, _clock);

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                BlobPath.Create(TenantA, "docs", "subject.txt"),
                Payload("data"),
                Options(retainUntil: _clock.UtcNow.AddDays(-1), subjectId: Subject,
                    classification: DataClassificationLevel.Phi),
                default);

            LifecycleOutcome outcome = (await sweep.RunAsync(["docs"], default)).Value;

            Assert.Equal(0, outcome.Deleted);
            Assert.Equal(1, outcome.HeldBack);
        }
    }

    // ── chunked upload ────────────────────────────────────────────────────

    [Fact]
    public async Task ChunkedUpload_AssemblesTheWholePayload()
    {
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "study.bin");

        using (ActAs(TenantA))
        {
            using IBlobUploadSession session = store.BeginUpload(path, Options()).Value;

            await session.AppendAsync(Encoding.UTF8.GetBytes("part-one "), default);
            await session.AppendAsync(Encoding.UTF8.GetBytes("part-two"), default);

            Assert.Equal(17, session.BytesReceived);
            Assert.True((await session.CompleteAsync(default)).IsSuccess);

            using BlobContent content = (await store.ReadAsync(path, default)).Value;
            using var reader = new StreamReader(content.Content);

            Assert.Equal("part-one part-two", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task ChunkedUpload_RefusesTheChunkThatCrossesTheLimit()
    {
        // Refused as it arrives, not after the whole thing has been buffered.
        TenantScopedBlobStore store = CreateStore();

        using (ActAs(TenantA))
        {
            var options = new BlobWriteOptions(DataClassificationLevel.Public, "text/plain", 10);
            using IBlobUploadSession session = store.BeginUpload(
                BlobPath.Create(TenantA, "big.bin"), options).Value;

            Assert.True((await session.AppendAsync(new byte[8], default)).IsSuccess);
            Assert.True((await session.AppendAsync(new byte[8], default)).IsFailure);
            Assert.True((await session.CompleteAsync(default)).IsFailure);
        }
    }

    [Fact]
    public async Task ChunkedUpload_AppliesScanningAtCompletion()
    {
        // Once, over the assembled payload. A scanner shown one chunk cannot
        // see a signature that straddles the boundary.
        var scanner = new StubScanner(ScanVerdict.Clean);
        TenantScopedBlobStore store = CreateStore(scanner);

        using (ActAs(TenantA))
        {
            using IBlobUploadSession session = store.BeginUpload(
                BlobPath.Create(TenantA, "study.bin"), Options()).Value;

            await session.AppendAsync(Encoding.UTF8.GetBytes("aa"), default);
            await session.AppendAsync(Encoding.UTF8.GetBytes("bb"), default);
            await session.CompleteAsync(default);
        }

        Assert.Equal(1, scanner.Invocations);
        Assert.Equal("aabb", Encoding.UTF8.GetString(scanner.LastScanned!));
    }

    [Fact]
    public void ChunkedUpload_WithNoResolvedTenant_IsRefused()
    {
        Result<IBlobUploadSession> session = CreateStore()
            .BeginUpload(BlobPath.Create(TenantA, "study.bin"), Options());

        Assert.True(session.IsFailure);
    }

    // ── streaming ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenReadAsync_StreamsAnUnencryptedBlob()
    {
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "public.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("streamable"), Options(), default);

            using BlobContent content = (await store.OpenReadAsync(path, default)).Value;
            using var reader = new StreamReader(content.Content);

            Assert.Equal("streamable", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task OpenReadAsync_RefusesAnEncryptedBlob()
    {
        // AES-GCM's tag covers the whole ciphertext and is checked at the end.
        // Streaming would hand the caller plaintext that has not yet been shown
        // to be authentic, and a tag failure a moment later does not un-act
        // whatever was done with it.
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                path, Payload("MRN-000123"), Options(classification: DataClassificationLevel.Phi), default);

            Result<BlobContent> streamed = await store.OpenReadAsync(path, default);

            Assert.True(streamed.IsFailure);
            Assert.Equal(ErrorCodes.CapabilityNotSupported, streamed.Error!.Code);
        }
    }

    // ── doubles ───────────────────────────────────────────────────────────

    private sealed class StubScanner(ScanVerdict verdict) : IContentScanner
    {
        public string ScannerName => "Stub";

        public int Invocations { get; private set; }

        public byte[]? LastScanned { get; private set; }

        public Task<Result<ScanVerdict>> ScanAsync(byte[] content, CancellationToken cancellationToken)
        {
            Invocations++;
            LastScanned = content;
            return Task.FromResult(Result<ScanVerdict>.FromValue(verdict));
        }
    }

    // ── OCR / text extraction ─────────────────────────────────────────────

    [Fact]
    public async Task ExtractText_WithNoExtractor_ReportsUnsupportedNotEmpty()
    {
        // "Nobody looked" and "this document contains no text" are different
        // facts, and only one of them is safe to act on.
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "scan.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("content"), Options(), default);

            Result<ExtractedContent> extracted = await store.ExtractTextAsync(path, default);

            Assert.True(extracted.IsFailure);
            Assert.Equal(ErrorCodes.CapabilityNotSupported, extracted.Error!.Code);
        }
    }

    [Fact]
    public async Task ExtractText_InheritsTheBlobsClassification()
    {
        // The seam where OCR pipelines leak: text goes to a search index that
        // was never told what it was handling.
        TenantScopedBlobStore store = CreateStore(extractor: new StubExtractor());
        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                path, Payload("MRN-000123"),
                Options(classification: DataClassificationLevel.Phi), default);

            ExtractedContent extracted = (await store.ExtractTextAsync(path, default)).Value;

            Assert.Equal(DataClassificationLevel.Phi, extracted.Classification);
        }
    }

    [Fact]
    public async Task ExtractText_OverridesAnExtractorThatUnderDeclares()
    {
        // An extractor is third-party code. If it claimed Public, the text and
        // the tables would enter a search index labelled Public.
        TenantScopedBlobStore store = CreateStore(
            extractor: new StubExtractor { DeclaredClassification = DataClassificationLevel.Public });

        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                path, Payload("MRN-000123"),
                Options(classification: DataClassificationLevel.Phi), default);

            ExtractedContent extracted = (await store.ExtractTextAsync(path, default)).Value;

            Assert.Equal(DataClassificationLevel.Phi, extracted.Classification);
        }
    }

    [Fact]
    public async Task ExtractText_SeesDecryptedDecompressedPlaintext()
    {
        var extractor = new StubExtractor();
        TenantScopedBlobStore store = CreateStore(extractor: extractor);
        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                path, Payload("readable text"),
                Options(compress: true, classification: DataClassificationLevel.Phi), default);

            await store.ExtractTextAsync(path, default);
        }

        Assert.Equal("readable text", Encoding.UTF8.GetString(extractor.LastSeen!));
    }

    [Fact]
    public async Task ExtractText_ReturnsTablesFieldsLanguageAndConfidence()
    {
        var extractor = new StubExtractor
        {
            Language = "bn",
            Fields = [new ExtractedField("NHS number", "943 476 5919", 0.97)],
            Tables = [new ExtractedTable([["Test", "Value"], ["Potassium", "6.9"]], 0.95)],
        };

        TenantScopedBlobStore store = CreateStore(extractor: extractor);
        BlobPath path = BlobPath.Create(TenantA, "lab.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("report"), Options(), default);

            ExtractedContent extracted = (await store.ExtractTextAsync(path, default)).Value;

            Assert.Equal("bn", extracted.Language);
            Assert.Equal("943 476 5919", Assert.Single(extracted.Fields).Value);
            Assert.Equal("6.9", Assert.Single(extracted.Tables).Rows[1][1]);
            Assert.False(extracted.RequiresHumanReview);
        }
    }

    [Fact]
    public async Task ExtractText_FlagsALowConfidenceFieldEvenWhenTheDocumentScoredWell()
    {
        // The case that matters. A discharge summary read at 0.98 overall can
        // carry one field at 0.41, and that field is the one holding the dose.
        var extractor = new StubExtractor
        {
            Confidence = 0.98,
            Fields = [new ExtractedField("Dose", "50mg", 0.41)],
        };

        TenantScopedBlobStore store = CreateStore(extractor: extractor);
        BlobPath path = BlobPath.Create(TenantA, "summary.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("summary"), Options(), default);

            ExtractedContent extracted = (await store.ExtractTextAsync(path, default)).Value;

            Assert.True(extracted.RequiresHumanReview);

            // Flagged, not discarded. Dropping it would lose the fact that the
            // document had a dose written on it at all.
            Assert.Equal("50mg", Assert.Single(extracted.Fields).Value);
        }
    }

    [Fact]
    public async Task ExtractText_FlagsALowConfidenceTable()
    {
        var extractor = new StubExtractor
        {
            Tables = [new ExtractedTable([["Potassium", "6.9"]], 0.5)],
        };

        TenantScopedBlobStore store = CreateStore(extractor: extractor);
        BlobPath path = BlobPath.Create(TenantA, "lab.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("report"), Options(), default);

            Assert.True((await store.ExtractTextAsync(path, default)).Value.RequiresHumanReview);
        }
    }

    [Fact]
    public async Task ExtractText_OfAnUnsupportedMediaType_IsRefused()
    {
        TenantScopedBlobStore store = CreateStore(
            extractor: new StubExtractor { Supported = ["application/pdf"] });

        BlobPath path = BlobPath.Create(TenantA, "note.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("text"), Options(), default);

            Result<ExtractedContent> extracted = await store.ExtractTextAsync(path, default);

            Assert.Equal(ErrorCodes.CapabilityNotSupported, extracted.Error!.Code);
        }
    }

    [Fact]
    public void ExtractedField_RefusesAConfidenceOutsideZeroToOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractedField("k", "v", 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractedField("k", "v", -0.1));
    }

    // ── audit: a failed audit fails the operation (BRL-005) ───────────────

    [Fact]
    public async Task Read_RecordsTheAccessBeforeReturningContent()
    {
        var audit = new RecordingAuditSink();
        TenantScopedBlobStore store = CreateStore(audit: audit);
        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                path, Payload("MRN-000123"),
                Options(classification: DataClassificationLevel.Phi), default);

            using BlobContent content = (await store.ReadAsync(path, default)).Value;
        }

        StorageAuditEvent read = Assert.Single(
            audit.Events, e => e.Operation == StorageOperation.Read);

        Assert.Equal(DataClassificationLevel.Phi, read.Classification);
        Assert.True(read.Succeeded);
        Assert.Equal(0, read.OccurredUtc.UtcTicks % 10);
    }

    [Fact]
    public async Task Read_WhenTheAuditSinkFails_ReturnsNoContent()
    {
        // HIPAA 164.312(b) requires the access record. Serving the bytes and
        // logging a warning produces a system whose audit trail is complete
        // only when nothing went wrong.
        TenantScopedBlobStore writeStore = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            await writeStore.WriteAsync(path, Payload("MRN-000123"), Options(), default);
        }

        TenantScopedBlobStore readStore = CreateStore(audit: new FailingAuditSink());

        using (ActAs(TenantA))
        {
            Result<BlobContent> read = await readStore.ReadAsync(path, default);

            Assert.True(read.IsFailure);
            Assert.Equal(ErrorCodes.AuditUnavailable, read.Error!.Code);
        }
    }

    [Fact]
    public async Task Write_WhenTheAuditSinkFails_LeavesNothingStored()
    {
        // An unaudited write is indistinguishable from one nobody performed,
        // and the store must not hold content it cannot account for.
        TenantScopedBlobStore store = CreateStore(audit: new FailingAuditSink());
        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                path, Payload("MRN-000123"), Options(), default);

            Assert.True(written.IsFailure);
        }

        Assert.Null(_backend.RawBytesAt(path.Value));
    }

    [Fact]
    public async Task Delete_IsRecordedEvenWhenItFails()
    {
        // "Did anyone try to destroy this record" is a question asked after an
        // incident, and a log of successes cannot answer it.
        var audit = new RecordingAuditSink();
        TenantScopedBlobStore store = CreateStore(audit: audit);

        using (ActAs(TenantA))
        {
            await store.DeleteAsync(BlobPath.Create(TenantA, "never-existed.txt"), default);
        }

        StorageAuditEvent deleted = Assert.Single(
            audit.Events, e => e.Operation == StorageOperation.Delete);

        Assert.False(deleted.Succeeded);
        Assert.Equal(ErrorCodes.NotFound, deleted.ErrorCode);
    }

    [Fact]
    public async Task AuditEvents_CarryAHashAndNeverContent()
    {
        // An audit trail over a clinical store is itself a target. The hash
        // identifies which bytes without being them.
        var audit = new RecordingAuditSink();
        TenantScopedBlobStore store = CreateStore(audit: audit);

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                BlobPath.Create(TenantA, "chart.txt"), Payload("MRN-000123"), Options(), default);
        }

        StorageAuditEvent written = Assert.Single(audit.Events);

        Assert.NotNull(written.ContentHash);
        Assert.DoesNotContain("MRN-000123", written.ContentHash!, StringComparison.Ordinal);
    }

    private sealed class RecordingAuditSink : IStorageAuditSink
    {
        public List<StorageAuditEvent> Events { get; } = [];

        public Task<Result> RecordAsync(StorageAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FailingAuditSink : IStorageAuditSink
    {
        public Task<Result> RecordAsync(StorageAuditEvent auditEvent, CancellationToken cancellationToken)
            => Task.FromResult(Result.Failure(new Error(
                ErrorCodes.AuditUnavailable,
                "The audit store is unreachable.",
                ErrorCategory.Transient)));
    }

    private sealed class StubExtractor : IContentExtractor
    {
        public string ExtractorName => "Stub";

        public IReadOnlyList<string> Supported { get; set; } = ["text/plain"];

        public IReadOnlyList<string> SupportedContentTypes => Supported;

        public DataClassificationLevel DeclaredClassification { get; set; } = DataClassificationLevel.Phi;

        public double Confidence { get; set; } = 0.99;

        public string? Language { get; set; }

        public IReadOnlyList<ExtractedField> Fields { get; set; } = [];

        public IReadOnlyList<ExtractedTable> Tables { get; set; } = [];

        public byte[]? LastSeen { get; private set; }

        public Task<Result<ExtractedContent>> ExtractAsync(
            byte[] content, string contentType, CancellationToken cancellationToken)
        {
            LastSeen = content;

            return Task.FromResult(Result<ExtractedContent>.FromValue(new ExtractedContent(
                Encoding.UTF8.GetString(content),
                DeclaredClassification,
                ExtractorName,
                Confidence,
                Language,
                Fields,
                Tables)));
        }
    }

    private sealed class FailingScanner : IContentScanner
    {
        public string ScannerName => "Failing";

        public Task<Result<ScanVerdict>> ScanAsync(byte[] content, CancellationToken cancellationToken)
            => Task.FromResult(Result.Failure<ScanVerdict>(new Error(
                ErrorCodes.TransientFailure, "The scanning service is unavailable.", ErrorCategory.Transient)));
    }

    private sealed class HoldEverything(FakeClock clock) : ILegalHoldStore
    {
        public Task<LegalHold?> FindActiveHoldAsync(
            Guid tenantId, string subjectToken, CancellationToken cancellationToken)
            => Task.FromResult<LegalHold?>(new LegalHold(
                "HOLD-2026-01",
                "Litigation hold pending disclosure.",
                clock.UtcNow.AddDays(-30),
                clock.UtcNow.AddYears(2)));
    }
}
