using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Storage;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;
using Edpf.Core.Time;

namespace Edpf.Storage;

/// <summary>
/// The policy layer: everything that must be true of a stored blob regardless
/// of where the bytes physically land (Phase 14 §④).
/// </summary>
/// <remarks>
/// <para>
/// This class is the reason a new backend is cheap. Tenant enforcement,
/// encryption at rest, content-type coercion, bounded reads and
/// platform-computed hashing live here, once. A backend author implements six
/// methods of raw I/O and inherits all of it — including the parts they have
/// never heard of.
/// </para>
/// <para>
/// **Fail closed on encryption.** If a caller declares
/// <see cref="DataClassificationLevel.Confidential"/> or above and no
/// <see cref="ICryptoProvider"/> was supplied, the write is refused. The
/// alternative — write it in the clear and log a warning — puts PHI on disk in
/// exchange for a line nobody reads.
/// </para>
/// </remarks>
public sealed class TenantScopedBlobStore : IBlobStore
{
    internal const string MetadataClassification = "edpf.classification";
    internal const string MetadataDeclaredContentType = "edpf.declared-content-type";
    internal const string MetadataServedContentType = "edpf.served-content-type";
    internal const string MetadataAttachment = "edpf.attachment";
    internal const string MetadataContentHash = "edpf.content-hash";
    internal const string MetadataLength = "edpf.length";
    internal const string MetadataEncrypted = "edpf.encrypted";
    internal const string MetadataCreatedUtc = "edpf.created-utc";
    internal const string MetadataScanState = "edpf.scan-state";
    internal const string MetadataScanner = "edpf.scanner";
    internal const string MetadataCompressed = "edpf.compressed";
    internal const string MetadataVersion = "edpf.version";
    internal const string MetadataRetainUntil = "edpf.retain-until";
    internal const string MetadataSubject = "edpf.subject";

    /// <summary>The suffix that marks a superseded version. Reserved.</summary>
    internal const string VersionSuffix = "__v";

    private const int CopyBufferSize = 81920;

    private readonly IBlobBackend _backend;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IDataProtectionPolicy _protection;
    private readonly IHashingService _hashing;
    private readonly IClock _clock;
    private readonly ICryptoProvider? _crypto;
    private readonly IContentScanner? _scanner;
    private readonly IContentExtractor? _extractor;

    /// <summary>
    /// Composes the policy over a backend.
    /// </summary>
    /// <param name="backend">The raw storage technology.</param>
    /// <param name="tenantAccessor">Ambient tenant. A null current tenant is a refusal, never "any tenant".</param>
    /// <param name="protection">
    /// The single classification-to-protection table. Required, and deliberately
    /// not defaulted: a store that supplied its own default would be a second
    /// opinion on what "classified" means.
    /// </param>
    /// <param name="hashing">Hashing seam (Z.10) — the platform never touches <c>System.Security.Cryptography</c> directly.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <param name="crypto">
    /// Encryption seam. Optional only in the sense that a deployment storing
    /// nothing the policy marks <c>EncryptAtRest</c> does not need one; omitting
    /// it makes every such write fail.
    /// </param>
    /// <param name="scanner">
    /// Malware scanning seam. When absent, blobs record
    /// <see cref="ScanState.NotScanned"/> — honestly, rather than being marked
    /// clean by a scanner that never ran.
    /// </param>
    /// <param name="extractor">
    /// Text-extraction seam — OCR, a PDF text layer, a document parser. When
    /// absent, <see cref="ExtractTextAsync"/> reports the capability as
    /// unsupported rather than returning nothing, so a caller cannot mistake
    /// "no extractor configured" for "this document contains no text".
    /// </param>
    /// <exception cref="ArgumentNullException">Any required dependency is null.</exception>
    public TenantScopedBlobStore(
        IBlobBackend backend,
        ITenantContextAccessor tenantAccessor,
        IDataProtectionPolicy protection,
        IHashingService hashing,
        IClock clock,
        ICryptoProvider? crypto = null,
        IContentScanner? scanner = null,
        IContentExtractor? extractor = null)
    {
        _backend = Guard.NotNull(backend, nameof(backend));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));
        _protection = Guard.NotNull(protection, nameof(protection));
        _hashing = Guard.NotNull(hashing, nameof(hashing));
        _clock = Guard.NotNull(clock, nameof(clock));
        _crypto = crypto;
        _scanner = scanner;
        _extractor = extractor;
    }

    /// <inheritdoc />
    public async Task<Result<BlobDescriptor>> WriteAsync(
        BlobPath path,
        Stream content,
        BlobWriteOptions options,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(content, nameof(content));
        Guard.NotNull(options, nameof(options));

        Result<Guid> tenant = RequireOwningTenant(path);
        if (tenant.IsFailure)
        {
            return Result.Failure<BlobDescriptor>(tenant.Error!);
        }

        if (IsVersionPath(path))
        {
            // The suffix is reserved so that a caller-chosen name can never
            // collide with an archived version and overwrite the history of a
            // different blob. Refused, not silently renamed.
            return Result.Failure<BlobDescriptor>(new Error(
                ErrorCodes.ValidationFailed,
                "That name ends in the reserved version suffix and cannot be written directly.",
                ErrorCategory.Validation));
        }

        DataProtectionRequirements required = _protection.For(options.Classification);
        bool mustEncrypt = Requires(required, DataProtectionRequirements.EncryptAtRest);

        // The policy's answer for payment data is "never hold it raw". A blob
        // is raw by definition — there is no tokenised form of a file — so the
        // only way to honour that requirement here is to refuse the write.
        //
        // This refusal was not designed; it fell out of consulting the table
        // instead of restating its threshold. A hand-written
        // `>= Confidential` rule would have encrypted the card data and
        // considered the obligation met.
        if (Requires(required, DataProtectionRequirements.TokenizeNeverStoreRaw))
        {
            return Result.Failure<BlobDescriptor>(new Error(
                ErrorCodes.ValidationFailed,
                "Content at this classification must never be stored in raw form, and a blob has no tokenised form.",
                ErrorCategory.Validation));
        }

        // Fail closed *before* reading a single byte. A classified payload that
        // cannot be encrypted should never have existed in this process's
        // memory, let alone reached a backend.
        if (mustEncrypt && _crypto is null)
        {
            return Result.Failure<BlobDescriptor>(new Error(
                ErrorCodes.CryptoFailure,
                "Storing data at this classification requires encryption at rest, and no crypto provider is configured.",
                ErrorCategory.Security));
        }

        Result<byte[]> plaintext = await ReadBoundedAsync(content, options.MaxLength, cancellationToken)
            .ConfigureAwait(false);
        if (plaintext.IsFailure)
        {
            return Result.Failure<BlobDescriptor>(plaintext.Error!);
        }

        byte[] bytes = plaintext.Value;
        string contentHash = ToHex(_hashing.Sha256(bytes));

        // ── Scan before anything else touches the payload ────────────────
        //
        // On the plaintext, and on the whole payload. Scanning ciphertext
        // finds nothing, and scanning a chunk misses a signature that
        // straddles the boundary.
        ScanState scanState = ScanState.NotScanned;
        if (_scanner is not null)
        {
            Result<ScanVerdict> verdict =
                await _scanner.ScanAsync(bytes, cancellationToken).ConfigureAwait(false);

            // A scanner failure and an Indeterminate verdict are the same
            // thing: the content was not cleared. Treating either as clean is
            // how a password-protected archive walks past a scanner.
            if (verdict.IsFailure || verdict.Value != ScanVerdict.Clean)
            {
                return Result.Failure<BlobDescriptor>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The content did not pass scanning and was not stored.",
                    ErrorCategory.Validation));
            }

            scanState = ScanState.Clean;
        }

        byte[] stored = bytes;

        // ── Compress, then encrypt ───────────────────────────────────────
        //
        // This order and not the reverse. Ciphertext is incompressible by
        // construction, so compressing after encrypting costs CPU and saves
        // nothing.
        if (options.Compress)
        {
            stored = BlobCompression.Compress(stored);
        }

        if (mustEncrypt)
        {
            KeyScope scope = options.SubjectId.HasValue
                ? KeyScope.ForSubject(tenant.Value, options.SubjectId.Value)
                : KeyScope.ForTenant(tenant.Value);

            Result<EncryptionEnvelope> envelope =
                await _crypto!.EncryptAsync(stored, scope, cancellationToken).ConfigureAwait(false);
            if (envelope.IsFailure)
            {
                return Result.Failure<BlobDescriptor>(envelope.Error!);
            }

            stored = envelope.Value.Serialize();
        }

        // ── Preserve what is already there ───────────────────────────────
        //
        // A write over an existing blob moves the current content aside first.
        // Overwriting a clinical document in place destroys the version a
        // clinician signed, and "we have a backup" is not the same as being
        // able to produce the exact bytes that were signed.
        int version = 1;
        Result<BlobDescriptor> existing = await StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing.IsSuccess)
        {
            version = existing.Value.Version + 1;

            Result archived = await ArchiveAsync(path, existing.Value, cancellationToken).ConfigureAwait(false);
            if (archived.IsFailure)
            {
                return Result.Failure<BlobDescriptor>(archived.Error!);
            }
        }

        ContentTypeDecision served = ServedContentType.Resolve(options.DeclaredContentType);

        // Normalised to the storable resolution the rest of the platform uses,
        // so a metadata store that later persists this cannot round-trip it to a
        // different instant than the one recorded (ADR-036).
        DateTimeOffset createdUtc = StorableInstant.Normalize(_clock.UtcNow);

        var descriptor = new BlobDescriptor(
            path,
            bytes.LongLength,
            contentHash,
            options.Classification,
            options.DeclaredContentType,
            served.ServedContentType,
            served.RequiresAttachment,
            mustEncrypt,
            createdUtc,
            scanState,
            options.Compress,
            version,
            options.RetainUntilUtc);

        Result put = await _backend.PutAsync(path, stored, cancellationToken).ConfigureAwait(false);
        if (put.IsFailure)
        {
            return Result.Failure<BlobDescriptor>(put.Error!);
        }

        Result metadata = await _backend
            .PutMetadataAsync(path, ToMetadata(descriptor, options.SubjectId), cancellationToken)
            .ConfigureAwait(false);
        if (metadata.IsFailure)
        {
            // The bytes landed and their description did not. Leaving the pair
            // half-written would produce a blob whose classification is unknown,
            // and unknown classification is treated as unclassified by anything
            // that reads it later.
            await _backend.RemoveAsync(path, cancellationToken).ConfigureAwait(false);
            return Result.Failure<BlobDescriptor>(metadata.Error!);
        }

        return descriptor;
    }

    /// <inheritdoc />
    public async Task<Result<BlobContent>> ReadAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<BlobDescriptor> descriptor = await StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (descriptor.IsFailure)
        {
            return Result.Failure<BlobContent>(descriptor.Error!);
        }

        Result<byte[]> stored = await _backend.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (stored.IsFailure)
        {
            return Result.Failure<BlobContent>(stored.Error!);
        }

        byte[] bytes = stored.Value;

        if (descriptor.Value.IsEncryptedAtRest)
        {
            if (_crypto is null)
            {
                return Result.Failure<BlobContent>(new Error(
                    ErrorCodes.CryptoFailure,
                    "The stored blob is encrypted and no crypto provider is configured to read it.",
                    ErrorCategory.Security));
            }

            EncryptionEnvelope envelope;
            try
            {
                envelope = EncryptionEnvelope.Deserialize(bytes);
            }
            catch (FormatException)
            {
                // Structural damage to the envelope. Reported as a crypto
                // failure carrying no structural detail: what is wrong with the
                // header is exactly what an attacker probing the format wants.
                return Result.Failure<BlobContent>(new Error(
                    ErrorCodes.CryptoFailure,
                    "The stored blob could not be decrypted.",
                    ErrorCategory.Security));
            }

            Result<byte[]> plaintext = await _crypto.DecryptAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (plaintext.IsFailure)
            {
                return Result.Failure<BlobContent>(plaintext.Error!);
            }

            bytes = plaintext.Value;
        }

        if (descriptor.Value.IsCompressed)
        {
            // Bounded by the recorded length, so a tampered or crafted archive
            // cannot expand into an unbounded buffer.
            Result<byte[]> expanded = BlobCompression.Decompress(
                bytes, Math.Max(descriptor.Value.Length, 1));

            if (expanded.IsFailure)
            {
                return Result.Failure<BlobContent>(expanded.Error!);
            }

            bytes = expanded.Value;
        }

        return new BlobContent(descriptor.Value, new MemoryStream(bytes, writable: false));
    }

    /// <inheritdoc />
    public async Task<Result<BlobDescriptor>> StatAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<Guid> tenant = RequireOwningTenant(path);
        if (tenant.IsFailure)
        {
            return Result.Failure<BlobDescriptor>(tenant.Error!);
        }

        Result<IReadOnlyDictionary<string, string>> metadata =
            await _backend.GetMetadataAsync(path, cancellationToken).ConfigureAwait(false);

        return metadata.IsFailure
            ? Result.Failure<BlobDescriptor>(metadata.Error!)
            : FromMetadata(path, metadata.Value);
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<Guid> tenant = RequireOwningTenant(path);
        if (tenant.IsFailure)
        {
            return Result.Failure(tenant.Error!);
        }

        return await _backend.RemoveAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BlobDescriptor>>> ListAsync(
        IReadOnlyList<string> prefixSegments,
        CancellationToken cancellationToken)
    {
        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Result.Failure<IReadOnlyList<BlobDescriptor>>(TenantRequired());
        }

        // The prefix is rendered through BlobPath, which means an enumeration
        // cannot be widened by a hostile segment any more than a read can:
        // "../../tenants" fails construction rather than escaping the root.
        string renderedPrefix;
        if (prefixSegments is null || prefixSegments.Count == 0)
        {
            renderedPrefix = "tenants/" + tenant.TenantId.ToString("D") + "/";
        }
        else
        {
            var segments = new string[prefixSegments.Count];
            for (int i = 0; i < prefixSegments.Count; i++)
            {
                segments[i] = prefixSegments[i];
            }

            BlobPath prefixPath;
            try
            {
                prefixPath = BlobPath.Create(tenant.TenantId, segments);
            }
            catch (ArgumentException)
            {
                return Result.Failure<IReadOnlyList<BlobDescriptor>>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The listing prefix is not a valid blob path.",
                    ErrorCategory.Validation));
            }

            renderedPrefix = prefixPath.Value + "/";
        }

        Result<IReadOnlyList<string>> paths =
            await _backend.ListAsync(renderedPrefix, cancellationToken).ConfigureAwait(false);
        if (paths.IsFailure)
        {
            return Result.Failure<IReadOnlyList<BlobDescriptor>>(paths.Error!);
        }

        var descriptors = new List<BlobDescriptor>(paths.Value.Count);
        foreach (string rendered in paths.Value)
        {
            Result<BlobPath> parsed = ParseRendered(rendered, tenant.TenantId);
            if (parsed.IsFailure)
            {
                continue;
            }

            // Archived versions are reachable through ListVersionsAsync, not
            // through a directory listing. A listing that returned every
            // superseded copy would make "the documents in this folder" a
            // number that grows every time one is edited.
            if (IsVersionPath(parsed.Value))
            {
                continue;
            }

            Result<BlobDescriptor> descriptor = await StatAsync(parsed.Value, cancellationToken).ConfigureAwait(false);
            if (descriptor.IsSuccess)
            {
                descriptors.Add(descriptor.Value);
            }
        }

        return descriptors;
    }

    private static Result<BlobPath> ParseRendered(string rendered, Guid tenantId)
    {
        string root = "tenants/" + tenantId.ToString("D") + "/";
        if (rendered is null || !rendered.StartsWith(root, StringComparison.Ordinal))
        {
            return Result.Failure<BlobPath>(NotFound());
        }

        string relative = rendered.Substring(root.Length);
        string[] segments = relative.Split('/');

        try
        {
            return BlobPath.Create(tenantId, segments);
        }
        catch (ArgumentException)
        {
            return Result.Failure<BlobPath>(NotFound());
        }
    }

    private Result<Guid> RequireOwningTenant(BlobPath path)
    {
        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Result.Failure<Guid>(TenantRequired());
        }

        return BlobPath.BelongsTo(path, tenant.TenantId)
            ? tenant.TenantId
            : Result.Failure<Guid>(TenantRequired());
    }

    private static async Task<Result<byte[]>> ReadBoundedAsync(
        Stream source,
        long maxLength,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[CopyBufferSize];
        long total = 0;

        while (true)
        {
            int read = await source.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxLength)
            {
                // Stop at the first byte over the line rather than after the
                // last one. A store that buffers the whole payload before
                // deciding it was too large has already paid the cost the limit
                // exists to avoid.
                return Result.Failure<byte[]>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The payload exceeds the declared maximum length.",
                    ErrorCategory.Validation));
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static Dictionary<string, string> ToMetadata(BlobDescriptor descriptor, Guid? subjectId)
        => new(StringComparer.Ordinal)
        {
            [MetadataClassification] = descriptor.Classification.ToString(),
            [MetadataDeclaredContentType] = descriptor.DeclaredContentType,
            [MetadataServedContentType] = descriptor.ServedContentType,
            [MetadataAttachment] = descriptor.RequiresAttachmentDisposition ? "1" : "0",
            [MetadataContentHash] = descriptor.ContentHash,
            [MetadataLength] = descriptor.Length.ToString(CultureInfo.InvariantCulture),
            [MetadataEncrypted] = descriptor.IsEncryptedAtRest ? "1" : "0",
            [MetadataCreatedUtc] = descriptor.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
            [MetadataScanState] = descriptor.ScanState.ToString(),
            [MetadataCompressed] = descriptor.IsCompressed ? "1" : "0",
            [MetadataVersion] = descriptor.Version.ToString(CultureInfo.InvariantCulture),
            [MetadataRetainUntil] = descriptor.RetainUntilUtc is null
                ? string.Empty
                : MetadataFormat.Instant(descriptor.RetainUntilUtc.Value),
            [MetadataSubject] = subjectId is null ? string.Empty : subjectId.Value.ToString("D"),
        };

    private static Result<BlobDescriptor> FromMetadata(
        BlobPath path,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata is null
            || !metadata.TryGetValue(MetadataClassification, out string? classificationText)
            || !Enum.TryParse(classificationText, out DataClassificationLevel classification)
            || !metadata.TryGetValue(MetadataDeclaredContentType, out string? declared)
            || !metadata.TryGetValue(MetadataServedContentType, out string? served)
            || !metadata.TryGetValue(MetadataContentHash, out string? hash)
            || !metadata.TryGetValue(MetadataLength, out string? lengthText)
            || !long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out long length)
            || !metadata.TryGetValue(MetadataCreatedUtc, out string? createdText)
            || !DateTimeOffset.TryParse(
                createdText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset created))
        {
            // Metadata that will not parse is not a blob with defaults. It is a
            // blob whose classification is unknown, and the one thing that must
            // never happen is treating that as Public.
            return Result.Failure<BlobDescriptor>(NotFound());
        }

        metadata.TryGetValue(MetadataAttachment, out string? attachment);
        metadata.TryGetValue(MetadataEncrypted, out string? encrypted);
        metadata.TryGetValue(MetadataCompressed, out string? compressed);

        // Absent or unparseable scan state reads as NotScanned, never as Clean.
        // The safe direction here is "we do not know", because a blob claiming
        // to be scanned by metadata nobody can parse has not been scanned.
        metadata.TryGetValue(MetadataScanState, out string? scanText);
        if (!Enum.TryParse(scanText, out ScanState scanState))
        {
            scanState = ScanState.NotScanned;
        }

        metadata.TryGetValue(MetadataVersion, out string? versionText);
        if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out int version))
        {
            version = 1;
        }

        metadata.TryGetValue(MetadataRetainUntil, out string? retainText);
        DateTimeOffset? retainUntil = MetadataFormat.TryInstant(retainText, out DateTimeOffset parsedRetain)
            ? parsedRetain
            : null;

        return new BlobDescriptor(
            path,
            length,
            hash!,
            classification,
            declared!,
            served!,
            string.Equals(attachment, "1", StringComparison.Ordinal),
            string.Equals(encrypted, "1", StringComparison.Ordinal),
            created,
            scanState,
            string.Equals(compressed, "1", StringComparison.Ordinal),
            version,
            retainUntil);
    }

    /// <summary>
    /// Starts a resumable chunked upload.
    /// </summary>
    /// <param name="path">The destination.</param>
    /// <param name="options">The caller's declarations, applied at completion.</param>
    /// <returns>The session, or a tenant refusal.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public Result<IBlobUploadSession> BeginUpload(BlobPath path, BlobWriteOptions options)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(options, nameof(options));

        Result<Guid> tenant = RequireOwningTenant(path);
        if (tenant.IsFailure)
        {
            return Result.Failure<IBlobUploadSession>(tenant.Error!);
        }

        // CA2000 cannot see through Result<T> that the session escapes to the
        // caller, who owns it — that is what a factory does, and it is why
        // IBlobUploadSession derives from IDisposable.
#pragma warning disable CA2000
        return Result<IBlobUploadSession>.FromValue(
            new BufferedUploadSession(this, path, options, Guid.NewGuid().ToString("N")));
#pragma warning restore CA2000
    }

    /// <summary>
    /// Opens a stream over a blob's content without buffering it whole.
    /// </summary>
    /// <param name="path">The tenant-scoped path.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The content, or a failure.</returns>
    /// <remarks>
    /// **Refused for an encrypted blob, deliberately.** AES-GCM's
    /// authentication tag covers the whole ciphertext and is verified at the
    /// end, so streaming would mean handing the caller plaintext that has not
    /// yet been shown to be authentic. Everything after that point is acting on
    /// data an attacker may have altered, and the fact that the tag check fails
    /// a moment later does not un-act it.
    /// </remarks>
    public async Task<Result<BlobContent>> OpenReadAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<BlobDescriptor> descriptor = await StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (descriptor.IsFailure)
        {
            return Result.Failure<BlobContent>(descriptor.Error!);
        }

        if (descriptor.Value.IsEncryptedAtRest)
        {
            return Result.Failure<BlobContent>(new Error(
                ErrorCodes.CapabilityNotSupported,
                "An encrypted blob cannot be streamed: its authenticity is established only once the whole "
                + "ciphertext has been read. Use ReadAsync.",
                ErrorCategory.Validation));
        }

        return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the archived versions of a blob, newest first.
    /// </summary>
    /// <param name="path">The live path.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>Descriptors of superseded versions.</returns>
    public async Task<Result<IReadOnlyList<BlobDescriptor>>> ListVersionsAsync(
        BlobPath path,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<Guid> tenant = RequireOwningTenant(path);
        if (tenant.IsFailure)
        {
            return Result.Failure<IReadOnlyList<BlobDescriptor>>(tenant.Error!);
        }

        var versions = new List<BlobDescriptor>();

        for (int version = 1; ; version++)
        {
            Result<BlobPath> archived = VersionPathFor(path, version);
            if (archived.IsFailure)
            {
                break;
            }

            Result<BlobDescriptor> descriptor =
                await StatAsync(archived.Value, cancellationToken).ConfigureAwait(false);

            if (descriptor.IsFailure)
            {
                break;
            }

            versions.Insert(0, descriptor.Value);
        }

        return versions;
    }

    /// <summary>
    /// Reads the subject a blob was written for, when it declared one.
    /// </summary>
    /// <param name="path">The tenant-scoped path.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The subject, null when none was declared, or a failure.</returns>
    /// <remarks>
    /// Used by the lifecycle sweep to ask the legal-hold store whether this
    /// blob's subject is under hold.
    /// </remarks>
    public async Task<Result<Guid?>> SubjectOfAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<Guid> tenant = RequireOwningTenant(path);
        if (tenant.IsFailure)
        {
            return Result.Failure<Guid?>(tenant.Error!);
        }

        Result<IReadOnlyDictionary<string, string>> metadata =
            await _backend.GetMetadataAsync(path, cancellationToken).ConfigureAwait(false);

        if (metadata.IsFailure)
        {
            return Result.Failure<Guid?>(metadata.Error!);
        }

        return metadata.Value.TryGetValue(MetadataSubject, out string? subject)
            && Guid.TryParse(subject, out Guid parsed)
                ? parsed
                : (Guid?)null;
    }

    /// <summary>
    /// Extracts searchable text from a stored blob.
    /// </summary>
    /// <param name="path">The tenant-scoped path.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>
    /// The extracted text, carrying the blob's classification, or a failure.
    /// </returns>
    /// <remarks>
    /// <para>
    /// **The extracted text inherits the blob's classification, and there is no
    /// argument that lowers it.** The text of a scanned discharge summary is
    /// the discharge summary. This is the seam where an OCR pipeline leaks in
    /// practice: text goes to a search index that was never told what it was
    /// handling, and the index has no ceiling of its own.
    /// </para>
    /// <para>
    /// With no extractor configured this reports
    /// <see cref="ErrorCodes.CapabilityNotSupported"/> rather than empty text,
    /// so a caller cannot mistake "nobody looked" for "this document contains
    /// no text" — the same distinction the scan state keeps.
    /// </para>
    /// </remarks>
    /// <param name="minimumConfidence">
    /// The floor below which a result must be checked by a person. Applied to
    /// the overall score and to every field and table independently — a
    /// document read at 0.98 overall can still carry one field at 0.41, and
    /// that field is the one holding the dose.
    /// </param>
    public async Task<Result<ExtractedContent>> ExtractTextAsync(
        BlobPath path,
        CancellationToken cancellationToken,
        double minimumConfidence = 0.8)
    {
        Guard.NotNull(path, nameof(path));

        if (_extractor is null)
        {
            return Result.Failure<ExtractedContent>(new Error(
                ErrorCodes.CapabilityNotSupported,
                "No text extractor is configured, so no claim can be made about this document's text.",
                ErrorCategory.Validation));
        }

        // Read through ReadAsync rather than the backend, so extraction gets
        // decrypted, decompressed plaintext and inherits the tenant check.
        Result<BlobContent> content = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (content.IsFailure)
        {
            return Result.Failure<ExtractedContent>(content.Error!);
        }

        using BlobContent blob = content.Value;

        if (!Supports(_extractor.SupportedContentTypes, blob.Descriptor.DeclaredContentType))
        {
            return Result.Failure<ExtractedContent>(new Error(
                ErrorCodes.CapabilityNotSupported,
                "The configured extractor does not read this media type.",
                ErrorCategory.Validation));
        }

        using var buffer = new MemoryStream();
        await blob.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        Result<ExtractedContent> extracted = await _extractor
            .ExtractAsync(buffer.ToArray(), blob.Descriptor.DeclaredContentType, cancellationToken)
            .ConfigureAwait(false);

        if (extracted.IsFailure)
        {
            return extracted;
        }

        // Re-stamped with the blob's classification rather than trusted from
        // the extractor. An extractor is third-party code; if it under-declared
        // what it produced, the text — and the tables, and the key-value pairs
        // — would enter a search index labelled Public.
        var result = new ExtractedContent(
            extracted.Value.Text,
            blob.Descriptor.Classification,
            extracted.Value.ExtractorName,
            extracted.Value.Confidence,
            extracted.Value.Language,
            extracted.Value.Fields,
            extracted.Value.Tables);

        // Three dispositions, matching ADR-029: usable passes, doubtful is
        // flagged for a person, and nothing is silently discarded. Dropping a
        // low-confidence field would lose the fact that the document had
        // something there at all, which is worse than reporting it uncertainly.
        if (BelowFloor(result, minimumConfidence))
        {
            result.FlagForHumanReview();
        }

        return result;
    }

    private static bool BelowFloor(ExtractedContent content, double minimumConfidence)
    {
        if (content.Confidence < minimumConfidence)
        {
            return true;
        }

        foreach (ExtractedField field in content.Fields)
        {
            if (field.Confidence < minimumConfidence)
            {
                return true;
            }
        }

        foreach (ExtractedTable table in content.Tables)
        {
            if (table.Confidence < minimumConfidence)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Supports(IReadOnlyList<string> supported, string contentType)
    {
        for (int i = 0; i < supported.Count; i++)
        {
            if (string.Equals(supported[i], contentType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<Result> ArchiveAsync(
        BlobPath path,
        BlobDescriptor current,
        CancellationToken cancellationToken)
    {
        Result<BlobPath> archivePath = VersionPathFor(path, current.Version);
        if (archivePath.IsFailure)
        {
            return Result.Failure(archivePath.Error!);
        }

        Result<byte[]> bytes = await _backend.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.IsFailure)
        {
            return Result.Failure(bytes.Error!);
        }

        Result<IReadOnlyDictionary<string, string>> metadata =
            await _backend.GetMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        if (metadata.IsFailure)
        {
            return Result.Failure(metadata.Error!);
        }

        Result put = await _backend.PutAsync(archivePath.Value, bytes.Value, cancellationToken).ConfigureAwait(false);
        return put.IsFailure
            ? put
            : await _backend
                .PutMetadataAsync(archivePath.Value, metadata.Value, cancellationToken)
                .ConfigureAwait(false);
    }

    private static Result<BlobPath> VersionPathFor(BlobPath path, int version)
    {
        string[] segments = path.RelativePath.Split('/');
        segments[segments.Length - 1] += VersionSuffix + version.ToString(CultureInfo.InvariantCulture);

        try
        {
            return BlobPath.Create(path.TenantId, segments);
        }
        catch (ArgumentException)
        {
            return Result.Failure<BlobPath>(NotFound());
        }
    }

    private static bool IsVersionPath(BlobPath path)
    {
        string[] segments = path.RelativePath.Split('/');
        string last = segments[segments.Length - 1];

        int marker = last.LastIndexOf(VersionSuffix, StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        string tail = last.Substring(marker + VersionSuffix.Length);
        if (tail.Length == 0)
        {
            return false;
        }

        foreach (char c in tail)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool Requires(DataProtectionRequirements set, DataProtectionRequirements flag)
        => (set & flag) == flag;

    private static string ToHex(byte[] digest)
    {
        const string HexDigits = "0123456789abcdef";
        var chars = new char[digest.Length * 2];

        for (int i = 0; i < digest.Length; i++)
        {
            chars[i * 2] = HexDigits[digest[i] >> 4];
            chars[(i * 2) + 1] = HexDigits[digest[i] & 0x0F];
        }

        return new string(chars);
    }

    /// <summary>
    /// The refusal for "not your tenant" and "no tenant resolved".
    /// </summary>
    /// <remarks>
    /// Same message and category as <see cref="NotFound"/>, so a caller
    /// building a response cannot accidentally reveal which of the two
    /// happened. The code differs so defenders can alert on it.
    /// </remarks>
    private static Error TenantRequired() => new(
        ErrorCodes.TenantScopeViolation,
        "The requested resource was not found.",
        ErrorCategory.NotFound);

    private static Error NotFound() => new(
        ErrorCodes.NotFound,
        "The requested resource was not found.",
        ErrorCategory.NotFound);
}
