using System.Reflection;
using System.Text;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Storage;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.Metadata;
using Edpf.Storage;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Storage;

/// <summary>
/// The storage policy layer (Phase 14, ADR-037 v1.0 addition 2). Everything
/// here is a property of <see cref="TenantScopedBlobStore"/> rather than of any
/// backend, which is the point: it holds for all sixteen of them or none.
/// </summary>
public sealed class BlobStorePolicyTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly InMemoryBlobBackend _backend = new();
    private readonly TenantContextAccessor _tenants = new();
    private readonly ReversibleTestCryptoProvider _crypto = new();
    private readonly FakeClock _clock = new();

    private TenantScopedBlobStore CreateStore(bool withCrypto = true, IBlobBackend? backend = null)
        => new(
            backend ?? _backend,
            _tenants,
            ProtectionPolicy.Default,
            new TestHashingService(),
            _clock,
            withCrypto ? _crypto : null);

    private IDisposable ActAs(Guid tenantId)
        => _tenants.Push(new TenantDescriptor(
            tenantId, "tenant", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()));

    private static MemoryStream Payload(string text) => new(Encoding.UTF8.GetBytes(text));

    private static BlobWriteOptions PublicText(long maxLength = 1024)
        => new(DataClassificationLevel.Public, "text/plain", maxLength);

    // ── tenancy ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_WithNoResolvedTenant_IsRefusedRatherThanTreatedAsAny()
    {
        TenantScopedBlobStore store = CreateStore();

        Result<BlobDescriptor> written = await store.WriteAsync(
            BlobPath.Create(TenantA, "a.txt"), Payload("x"), PublicText(), default);

        Assert.True(written.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, written.Error!.Code);
    }

    [Fact]
    public async Task ReadAsync_OfAnotherTenantsBlob_IsIndistinguishableFromAbsent()
    {
        TenantScopedBlobStore store = CreateStore();
        BlobPath owned = BlobPath.Create(TenantA, "notes.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(owned, Payload("secret"), PublicText(), default);
        }

        Result<BlobContent> crossTenant;
        Result<BlobContent> absent;
        using (ActAs(TenantB))
        {
            crossTenant = await store.ReadAsync(owned, default);
            absent = await store.ReadAsync(BlobPath.Create(TenantB, "never-written.txt"), default);
        }

        Assert.True(crossTenant.IsFailure);
        Assert.True(absent.IsFailure);

        // What a caller can put on the wire is identical, so the store answers
        // "does tenant A have a file called notes.txt" with silence.
        Assert.Equal(absent.Error!.Message, crossTenant.Error!.Message);
        Assert.Equal(absent.Error.Category, crossTenant.Error.Category);

        // The internal code differs on purpose — a defender needs to see path
        // walking; the attacker still cannot.
        Assert.Equal(ErrorCodes.TenantScopeViolation, crossTenant.Error.Code);
        Assert.Equal(ErrorCodes.NotFound, absent.Error.Code);
    }

    [Fact]
    public async Task DeleteAsync_OfAnotherTenantsBlob_DoesNotDeleteIt()
    {
        // The check has to precede the operation, not the response. A store
        // that deletes and then reports not-found has destroyed data it was
        // refusing to admit existed.
        TenantScopedBlobStore store = CreateStore();
        BlobPath owned = BlobPath.Create(TenantA, "notes.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(owned, Payload("keep me"), PublicText(), default);
        }

        using (ActAs(TenantB))
        {
            Assert.True((await store.DeleteAsync(owned, default)).IsFailure);
        }

        using (ActAs(TenantA))
        {
            Assert.True((await store.ReadAsync(owned, default)).IsSuccess);
        }
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyTheCurrentTenantsBlobs()
    {
        TenantScopedBlobStore store = CreateStore();

        using (ActAs(TenantA))
        {
            await store.WriteAsync(BlobPath.Create(TenantA, "docs", "a.txt"), Payload("a"), PublicText(), default);
        }

        using (ActAs(TenantB))
        {
            await store.WriteAsync(BlobPath.Create(TenantB, "docs", "b.txt"), Payload("b"), PublicText(), default);

            Result<IReadOnlyList<BlobDescriptor>> listed = await store.ListAsync(["docs"], default);

            Assert.True(listed.IsSuccess);
            BlobDescriptor only = Assert.Single(listed.Value);
            Assert.Equal(TenantB, only.Path.TenantId);
        }
    }

    [Fact]
    public async Task ListAsync_WithATraversalPrefix_IsRefused()
    {
        TenantScopedBlobStore store = CreateStore();

        using (ActAs(TenantA))
        {
            Result<IReadOnlyList<BlobDescriptor>> listed = await store.ListAsync(["..", ".."], default);

            Assert.True(listed.IsFailure);
            Assert.Equal(ErrorCodes.ValidationFailed, listed.Error!.Code);
        }
    }

    [Fact]
    public async Task ListAsync_WithNoResolvedTenant_IsRefused()
    {
        TenantScopedBlobStore store = CreateStore();

        Result<IReadOnlyList<BlobDescriptor>> listed = await store.ListAsync([], default);

        Assert.True(listed.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, listed.Error!.Code);
    }

    // ── classification drives protection ──────────────────────────────────

    [Fact]
    public async Task WriteAsync_OfPhi_StoresCiphertext_NotPlaintext()
    {
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                path,
                Payload("MRN-000123"),
                new BlobWriteOptions(DataClassificationLevel.Phi, "text/plain", 1024),
                default);

            Assert.True(written.IsSuccess);
            Assert.True(written.Value.IsEncryptedAtRest);
        }

        byte[] raw = Assert.IsType<byte[]>(_backend.RawBytesAt(path.Value));
        Assert.DoesNotContain("MRN-000123", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_OfAnEncryptedBlob_ReturnsThePlaintext()
    {
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "chart.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                path,
                Payload("MRN-000123"),
                new BlobWriteOptions(DataClassificationLevel.Phi, "text/plain", 1024),
                default);

            using BlobContent content = (await store.ReadAsync(path, default)).Value;
            using var reader = new StreamReader(content.Content);

            Assert.Equal("MRN-000123", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task WriteAsync_OfClassifiedContent_WithoutACryptoProvider_IsRefused()
    {
        // Fail closed. The alternative is PHI on disk in the clear plus a log
        // line, and the log line has never once prevented a breach.
        TenantScopedBlobStore store = CreateStore(withCrypto: false);

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                BlobPath.Create(TenantA, "chart.txt"),
                Payload("MRN-000123"),
                new BlobWriteOptions(DataClassificationLevel.Phi, "text/plain", 1024),
                default);

            Assert.True(written.IsFailure);
            Assert.Equal(ErrorCodes.CryptoFailure, written.Error!.Code);
        }

        Assert.Null(_backend.RawBytesAt(BlobPath.Create(TenantA, "chart.txt").Value));
    }

    [Fact]
    public async Task WriteAsync_OfPaymentData_IsRefusedOutright()
    {
        // The protection table says payment data must never be stored raw. A
        // blob has no tokenised form, so honouring that means refusing — not
        // encrypting and calling it done.
        TenantScopedBlobStore store = CreateStore();

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                BlobPath.Create(TenantA, "card.txt"),
                Payload("4111111111111111"),
                new BlobWriteOptions(DataClassificationLevel.Pci, "text/plain", 1024),
                default);

            Assert.True(written.IsFailure);
            Assert.Equal(ErrorCodes.ValidationFailed, written.Error!.Code);
        }
    }

    [Fact]
    public async Task EncryptionDecision_ComesFromTheProtectionPolicy_AtEveryLevel()
    {
        // The regression this guards: a second threshold written inside the
        // storage layer. It would read `>= Confidential`, agree with the policy
        // at five levels out of six, and encrypt card data that the policy says
        // must not be stored at all.
        TenantScopedBlobStore store = CreateStore();

        foreach (DataClassificationLevel level in Enum.GetValues<DataClassificationLevel>())
        {
            DataProtectionRequirements required = ProtectionPolicy.Default.For(level);
            bool policyEncrypts = (required & DataProtectionRequirements.EncryptAtRest)
                == DataProtectionRequirements.EncryptAtRest;
            bool policyForbidsRaw = (required & DataProtectionRequirements.TokenizeNeverStoreRaw)
                == DataProtectionRequirements.TokenizeNeverStoreRaw;

            var path = BlobPath.Create(TenantA, "level-" + level.ToString());

            using (ActAs(TenantA))
            {
                Result<BlobDescriptor> written = await store.WriteAsync(
                    path, Payload("value"), new BlobWriteOptions(level, "text/plain", 1024), default);

                if (policyForbidsRaw)
                {
                    Assert.True(written.IsFailure, level.ToString());
                    continue;
                }

                Assert.True(written.IsSuccess, level.ToString());
                Assert.Equal(policyEncrypts, written.Value.IsEncryptedAtRest);
            }
        }
    }

    [Fact]
    public async Task WriteAsync_WithASubject_BindsTheBlobToTheSubjectKey()
    {
        // Crypto-shredding is per subject (ADR-006). A blob written under the
        // tenant key survives the subject's erasure, which is the difference
        // between "erased" and "erased except the scans".
        TenantScopedBlobStore store = CreateStore();
        Guid subject = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(
                BlobPath.Create(TenantA, "scan.bin"),
                Payload("image"),
                new BlobWriteOptions(DataClassificationLevel.Phi, "image/png", 1024, subject),
                default);
        }

        Assert.Equal(KeyScope.ForSubject(TenantA, subject), _crypto.LastScope);
    }

    // ── the served content type ───────────────────────────────────────────

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/xhtml+xml")]
    [InlineData("text/xml")]
    [InlineData("application/javascript")]
    [InlineData("text/javascript")]
    [InlineData("application/pdf")]
    public void Resolve_ForATypeThatCanExecuteOrEmbed_FallsBackToADownload(string declared)
    {
        ContentTypeDecision decision = ServedContentType.Resolve(declared);

        Assert.Equal(ServedContentType.Fallback, decision.ServedContentType);
        Assert.True(decision.RequiresAttachment);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("text/plain")]
    public void Resolve_ForARasterImageOrPlainText_RendersInline(string declared)
    {
        ContentTypeDecision decision = ServedContentType.Resolve(declared);

        Assert.Equal(declared, decision.ServedContentType);
        Assert.False(decision.RequiresAttachment);
    }

    [Fact]
    public void Resolve_DiscardsParametersAndNormalisesCase()
    {
        ContentTypeDecision decision = ServedContentType.Resolve("IMAGE/PNG; charset=utf-8");

        Assert.Equal("image/png", decision.ServedContentType);
        Assert.False(decision.RequiresAttachment);
    }

    [Theory]
    [InlineData("image/png\r\nSet-Cookie: session=stolen")]
    [InlineData("image/png\nX-Frame-Options: ALLOWALL")]
    [InlineData("image//png")]
    [InlineData("image")]
    [InlineData("/png")]
    [InlineData("image/")]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ForAMalformedOrInjectedType_FallsBack(string declared)
    {
        ContentTypeDecision decision = ServedContentType.Resolve(declared);

        Assert.Equal(ServedContentType.Fallback, decision.ServedContentType);
        Assert.True(decision.RequiresAttachment);
    }

    [Fact]
    public async Task WriteAsync_RecordsTheDeclaredTypeAndTheServedTypeSeparately()
    {
        // Both are kept. The declared one is evidence of what the uploader
        // claimed; discarding it would lose the audit trail for the coercion.
        TenantScopedBlobStore store = CreateStore();

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                BlobPath.Create(TenantA, "page.html"),
                Payload("<script>alert(1)</script>"),
                new BlobWriteOptions(DataClassificationLevel.Public, "text/html", 1024),
                default);

            Assert.Equal("text/html", written.Value.DeclaredContentType);
            Assert.Equal(ServedContentType.Fallback, written.Value.ServedContentType);
            Assert.True(written.Value.RequiresAttachmentDisposition);
        }
    }

    // ── bounds and integrity ──────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_BeyondTheDeclaredMaximum_IsRefusedAndStoresNothing()
    {
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "big.txt");

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                path, Payload(new string('x', 100)), PublicText(maxLength: 10), default);

            Assert.True(written.IsFailure);
            Assert.Equal(ErrorCodes.ValidationFailed, written.Error!.Code);
        }

        Assert.Null(_backend.RawBytesAt(path.Value));
    }

    [Fact]
    public async Task WriteAsync_ExactlyAtTheDeclaredMaximum_Succeeds()
    {
        // Off-by-one in the safe direction is still a bug: a limit that rejects
        // the size it advertises is a limit nobody can code against.
        TenantScopedBlobStore store = CreateStore();

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                BlobPath.Create(TenantA, "exact.txt"),
                Payload(new string('x', 10)),
                PublicText(maxLength: 10),
                default);

            Assert.True(written.IsSuccess);
            Assert.Equal(10, written.Value.Length);
        }
    }

    [Fact]
    public async Task ContentHash_IsComputedFromTheBytesReceived()
    {
        TenantScopedBlobStore store = CreateStore();
        byte[] payload = Encoding.UTF8.GetBytes("hello");
        string expected = Convert.ToHexString(new TestHashingService().Sha256(payload)).ToLowerInvariant();

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                BlobPath.Create(TenantA, "h.txt"), new MemoryStream(payload), PublicText(), default);

            Assert.Equal(expected, written.Value.ContentHash);
        }
    }

    [Fact]
    public void BlobWriteOptions_RefusesAnUnusableDeclaration()
    {
        Assert.Throws<ArgumentException>(
            () => new BlobWriteOptions(DataClassificationLevel.Public, "  ", 1024));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BlobWriteOptions(DataClassificationLevel.Public, "text/plain", 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BlobWriteOptions(
                DataClassificationLevel.Public, "text/plain", BlobWriteOptions.AbsoluteMaxLength + 1));
        Assert.Throws<ArgumentException>(
            () => new BlobWriteOptions(DataClassificationLevel.Public, "text/plain", 1024, Guid.Empty));
    }

    // ── structural ────────────────────────────────────────────────────────

    [Fact]
    public void NoBackend_IsAlsoABlobStore()
    {
        // If a backend implemented IBlobStore, registering it directly in the
        // container would compile — and the whole policy layer would be
        // bypassed by one plausible-looking line of composition code. This
        // keeps that a compile error rather than a code review.
        var offenders = typeof(TenantScopedBlobStore).Assembly
            .GetTypes()
            .Where(t => typeof(IBlobBackend).IsAssignableFrom(t) && typeof(IBlobStore).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task WriteAsync_WhenMetadataCannotBeStored_LeavesNoOrphanedBytes()
    {
        // A blob without metadata has no recorded classification, and anything
        // reading it later has to guess. Guessing low is a disclosure; guessing
        // high is an outage. Neither is acceptable, so the pair is all-or-nothing.
        var inner = new InMemoryBlobBackend();
        TenantScopedBlobStore store = CreateStore(backend: new FailingMetadataBackend(inner));
        BlobPath path = BlobPath.Create(TenantA, "orphan.txt");

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> written = await store.WriteAsync(
                path, Payload("content"), PublicText(), default);

            Assert.True(written.IsFailure);
        }

        Assert.Null(inner.RawBytesAt(path.Value));
    }

    [Fact]
    public async Task StatAsync_WhenMetadataIsUnparseable_ReportsNotFoundRatherThanDefaulting()
    {
        // Unknown classification must never resolve to Public. This is the
        // failure mode where a corrupted sidecar quietly downgrades PHI.
        TenantScopedBlobStore store = CreateStore();
        BlobPath path = BlobPath.Create(TenantA, "corrupt.txt");

        using (ActAs(TenantA))
        {
            await store.WriteAsync(path, Payload("x"), PublicText(), default);
        }

        await _backend.PutMetadataAsync(
            path,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["edpf.classification"] = "NotALevel" },
            default);

        using (ActAs(TenantA))
        {
            Result<BlobDescriptor> stat = await store.StatAsync(path, default);

            Assert.True(stat.IsFailure);
            Assert.Equal(ErrorCodes.NotFound, stat.Error!.Code);
        }
    }

    [Fact]
    public void IBlobStore_HasNoOverloadThatAcceptsAPathAsAString()
    {
        // The tenancy guarantee rests on BlobPath being the only way in. One
        // convenience overload taking a string would reintroduce every
        // traversal the type was built to make unconstructable.
        MethodInfo[] methods = typeof(IBlobStore).GetMethods();

        foreach (MethodInfo method in methods)
        {
            if (method.Name is nameof(IBlobStore.ListAsync))
            {
                continue;
            }

            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(string));
        }
    }
}
