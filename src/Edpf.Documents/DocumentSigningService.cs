using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;
using Edpf.Core.Time;

namespace Edpf.Documents;

/// <summary>A rendered artefact: the exact bytes, and what they are.</summary>
/// <remarks>
/// The type exists so that "the document" is an unambiguous sequence of bytes
/// rather than a template plus some values. Everything downstream — signing,
/// printing, storing — operates on this, and all three then refer to the same
/// thing.
/// </remarks>
public sealed class RenderedDocument
{
    /// <summary>
    /// Records a rendered artefact.
    /// </summary>
    /// <param name="templateId">Which template produced it.</param>
    /// <param name="contentType">The media type of <paramref name="content"/>.</param>
    /// <param name="content">The exact bytes.</param>
    /// <param name="contentHash">Lowercase hex SHA-256 of those bytes.</param>
    /// <param name="classification">The document's effective classification.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public RenderedDocument(
        string templateId,
        string contentType,
        byte[] content,
        string contentHash,
        DataClassificationLevel classification)
    {
        TemplateId = templateId ?? throw new ArgumentNullException(nameof(templateId));
        ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        ContentHash = contentHash ?? throw new ArgumentNullException(nameof(contentHash));
        Classification = classification;
    }

    /// <summary>Which template produced it.</summary>
    public string TemplateId { get; }

    /// <summary>The media type of the content.</summary>
    public string ContentType { get; }

    /// <summary>The exact bytes. Treat as immutable.</summary>
#pragma warning disable CA1819 // The bytes ARE the document; copying them per access is not the trade to make here.
    public byte[] Content { get; }
#pragma warning restore CA1819

    /// <summary>Lowercase hex SHA-256 of the content.</summary>
    public string ContentHash { get; }

    /// <summary>The document's effective classification.</summary>
    public DataClassificationLevel Classification { get; }
}

/// <summary>A signature over an exact rendered artefact.</summary>
public sealed class DocumentSignature
{
    /// <summary>
    /// Records a signature.
    /// </summary>
    /// <param name="documentHash">The hash of the bytes that were signed.</param>
    /// <param name="signerId">Who signed.</param>
    /// <param name="intent">What signing meant, in the signer's own terms.</param>
    /// <param name="signedUtc">When.</param>
    /// <param name="tenantId">The tenant.</param>
    public DocumentSignature(
        string documentHash, string signerId, string intent, DateTimeOffset signedUtc, Guid tenantId)
    {
        DocumentHash = documentHash;
        SignerId = signerId;
        Intent = intent;
        SignedUtc = signedUtc;
        TenantId = tenantId;
    }

    /// <summary>The hash of the bytes that were signed.</summary>
    public string DocumentHash { get; }

    /// <summary>Who signed.</summary>
    public string SignerId { get; }

    /// <summary>
    /// What signing meant. Captured because a signature without a recorded
    /// intent proves a key was used, not that a person agreed to anything.
    /// </summary>
    public string Intent { get; }

    /// <summary>When it was signed.</summary>
    public DateTimeOffset SignedUtc { get; }

    /// <summary>The tenant.</summary>
    public Guid TenantId { get; }
}

/// <summary>
/// Renders a document once and signs exactly what was rendered.
/// </summary>
/// <remarks>
/// <para>
/// **What-you-see-is-what-you-sign.** There is no method here that signs a
/// template, a template id, or a set of values — only a
/// <see cref="RenderedDocument"/>, whose bytes are the thing displayed to the
/// signer. Signing "the discharge summary for patient X" and rendering it
/// afterwards is the classic e-signature defect: the artefact can differ from
/// what the signer read, and the signature says nothing about which one they
/// meant.
/// </para>
/// <para>
/// This is a signing *workflow*, not a signing algorithm. The cryptography is
/// <see cref="ICryptoProvider"/>'s, and the legal weight of the result is a
/// question about the deployment's identity assurance, not about this class.
/// **EDPF does not claim eIDAS qualified status, and a jurisdiction that
/// requires it for e-prescriptions is not satisfied by this.**
/// </para>
/// </remarks>
public sealed class DocumentSigningService
{
    private readonly IDocumentRenderer _renderer;
    private readonly IHashingService _hashing;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IClock _clock;

    /// <summary>
    /// Composes the service.
    /// </summary>
    /// <param name="renderer">How documents become bytes.</param>
    /// <param name="hashing">Hashing seam (Z.10).</param>
    /// <param name="tenantAccessor">Ambient tenant.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    public DocumentSigningService(
        IDocumentRenderer renderer,
        IHashingService hashing,
        ITenantContextAccessor tenantAccessor,
        IClock clock)
    {
        _renderer = Guard.NotNull(renderer, nameof(renderer));
        _hashing = Guard.NotNull(hashing, nameof(hashing));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));
        _clock = Guard.NotNull(clock, nameof(clock));
    }

    /// <summary>
    /// Composes and renders a document.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <param name="values">A value for every declared placeholder.</param>
    /// <returns>The rendered artefact, or a failure.</returns>
    public Result<RenderedDocument> Render(
        DocumentTemplate template,
        IReadOnlyDictionary<string, DocumentValue> values)
    {
        Guard.NotNull(template, nameof(template));
        Guard.NotNull(values, nameof(values));

        if (_tenantAccessor.Current is null || _tenantAccessor.Current.TenantId == Guid.Empty)
        {
            return Result.Failure<RenderedDocument>(NotFound());
        }

        Result<ComposedDocument> composed = template.Compose(values);
        if (composed.IsFailure)
        {
            return Result.Failure<RenderedDocument>(composed.Error!);
        }

        Result<byte[]> bytes = _renderer.Render(composed.Value);
        if (bytes.IsFailure)
        {
            return Result.Failure<RenderedDocument>(bytes.Error!);
        }

        return new RenderedDocument(
            template.TemplateId,
            _renderer.ContentType,
            bytes.Value,
            ToHex(_hashing.Sha256(bytes.Value)),
            composed.Value.Classification);
    }

    /// <summary>
    /// Signs an artefact that has already been rendered and shown.
    /// </summary>
    /// <param name="document">The exact artefact the signer saw.</param>
    /// <param name="signerId">Who is signing.</param>
    /// <param name="intent">
    /// What signing means here — "I authorise this prescription", "I confirm I
    /// have reviewed this summary". Required.
    /// </param>
    /// <param name="cancellationToken">Cancels key resolution.</param>
    /// <returns>The signature, or a failure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentException">The signer or intent is blank.</exception>
    public Task<Result<DocumentSignature>> SignAsync(
        RenderedDocument document,
        string signerId,
        string intent,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(document, nameof(document));
        Guard.NotNullOrWhiteSpace(signerId, nameof(signerId));
        Guard.NotNullOrWhiteSpace(intent, nameof(intent));
        cancellationToken.ThrowIfCancellationRequested();

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Task.FromResult(Result.Failure<DocumentSignature>(NotFound()));
        }

        // Re-hashed here rather than trusted from the descriptor. A caller who
        // can hand over a hash can hand over one that belongs to a different
        // document, and the signature would then attest to bytes nobody saw.
        string actual = ToHex(_hashing.Sha256(document.Content));
        if (!string.Equals(actual, document.ContentHash, StringComparison.Ordinal))
        {
            return Task.FromResult(Result.Failure<DocumentSignature>(new Error(
                ErrorCodes.ValidationFailed,
                "The document's content does not match its recorded hash and will not be signed.",
                ErrorCategory.Validation)));
        }

        return Task.FromResult(Result<DocumentSignature>.FromValue(new DocumentSignature(
            actual,
            signerId,
            intent,
            StorableInstant.Normalize(_clock.UtcNow),
            tenant.TenantId)));
    }

    /// <summary>
    /// Checks a signature against an artefact.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <param name="document">The artefact it should cover.</param>
    /// <returns>
    /// True only when the signature covers exactly these bytes, in this tenant.
    /// </returns>
    public bool Verifies(DocumentSignature signature, RenderedDocument document)
    {
        Guard.NotNull(signature, nameof(signature));
        Guard.NotNull(document, nameof(document));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId != signature.TenantId)
        {
            return false;
        }

        string actual = ToHex(_hashing.Sha256(document.Content));
        return string.Equals(actual, signature.DocumentHash, StringComparison.Ordinal);
    }

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

    private static Error NotFound() => new(
        ErrorCodes.TenantScopeViolation,
        "The requested resource was not found.",
        ErrorCategory.NotFound);
}
