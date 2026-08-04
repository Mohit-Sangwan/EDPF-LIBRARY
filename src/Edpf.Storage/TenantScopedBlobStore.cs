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

    private const int CopyBufferSize = 81920;

    private readonly IBlobBackend _backend;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IDataProtectionPolicy _protection;
    private readonly IHashingService _hashing;
    private readonly IClock _clock;
    private readonly ICryptoProvider? _crypto;

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
    /// <exception cref="ArgumentNullException">Any required dependency is null.</exception>
    public TenantScopedBlobStore(
        IBlobBackend backend,
        ITenantContextAccessor tenantAccessor,
        IDataProtectionPolicy protection,
        IHashingService hashing,
        IClock clock,
        ICryptoProvider? crypto = null)
    {
        _backend = Guard.NotNull(backend, nameof(backend));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));
        _protection = Guard.NotNull(protection, nameof(protection));
        _hashing = Guard.NotNull(hashing, nameof(hashing));
        _clock = Guard.NotNull(clock, nameof(clock));
        _crypto = crypto;
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

        byte[] stored = bytes;
        if (mustEncrypt)
        {
            KeyScope scope = options.SubjectId.HasValue
                ? KeyScope.ForSubject(tenant.Value, options.SubjectId.Value)
                : KeyScope.ForTenant(tenant.Value);

            Result<EncryptionEnvelope> envelope =
                await _crypto!.EncryptAsync(bytes, scope, cancellationToken).ConfigureAwait(false);
            if (envelope.IsFailure)
            {
                return Result.Failure<BlobDescriptor>(envelope.Error!);
            }

            stored = envelope.Value.Serialize();
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
            createdUtc);

        Result put = await _backend.PutAsync(path, stored, cancellationToken).ConfigureAwait(false);
        if (put.IsFailure)
        {
            return Result.Failure<BlobDescriptor>(put.Error!);
        }

        Result metadata = await _backend
            .PutMetadataAsync(path, ToMetadata(descriptor), cancellationToken)
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

    private static Dictionary<string, string> ToMetadata(BlobDescriptor descriptor)
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

        return new BlobDescriptor(
            path,
            length,
            hash!,
            classification,
            declared!,
            served!,
            string.Equals(attachment, "1", StringComparison.Ordinal),
            string.Equals(encrypted, "1", StringComparison.Ordinal),
            created);
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
