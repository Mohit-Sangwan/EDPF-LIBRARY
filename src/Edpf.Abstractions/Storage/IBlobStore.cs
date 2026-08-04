using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Storage;

/// <summary>
/// The tenant-safe blob store: the only supported way application code reaches
/// stored files (Phase 14).
/// </summary>
/// <remarks>
/// <para>
/// A store is a <see cref="IBlobBackend"/> plus policy. The policy — tenant
/// enforcement, encryption at rest, content-type coercion, bounded reads,
/// caller-independent hashing — is written once and applies to every backend,
/// present and future.
/// </para>
/// <para>
/// That split is deliberate and is the answer to "sixteen storage backends".
/// Each backend is roughly a hundred lines of I/O and inherits the security
/// properties whether or not its author understood them. A backend that
/// implemented the policy itself would be sixteen chances to get tenancy
/// wrong.
/// </para>
/// </remarks>
public interface IBlobStore
{
    /// <summary>
    /// Writes a blob, replacing any blob already at that path.
    /// </summary>
    /// <param name="path">The tenant-scoped destination.</param>
    /// <param name="content">The source stream. Read at most <see cref="BlobWriteOptions.MaxLength"/> bytes.</param>
    /// <param name="options">The caller's declarations. See <see cref="BlobWriteOptions"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// The descriptor of what was stored, or failure with
    /// <see cref="ErrorCodes.TenantScopeViolation"/> (the path belongs to
    /// another tenant, or no tenant is resolved),
    /// <see cref="ErrorCodes.ValidationFailed"/> (the payload exceeded the
    /// declared maximum), or <see cref="ErrorCodes.CryptoFailure"/> (the
    /// classification requires encryption and none is available).
    /// </returns>
    Task<Result<BlobDescriptor>> WriteAsync(
        BlobPath path,
        Stream content,
        BlobWriteOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a blob's plaintext.
    /// </summary>
    /// <param name="path">The tenant-scoped path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The content, or failure. **A blob belonging to another tenant is
    /// indistinguishable from one that does not exist**: identical message,
    /// identical <see cref="ErrorCategory"/>, so the response a caller can build
    /// is the same and the store is not an existence oracle.
    /// <para>
    /// The <see cref="Error.Code"/> does differ —
    /// <see cref="ErrorCodes.TenantScopeViolation"/> against
    /// <see cref="ErrorCodes.NotFound"/> — and that is deliberate rather than an
    /// oversight. Nothing puts the code on the wire, but a security team needs
    /// to alert on "someone is walking another tenant's paths", and a control
    /// that is invisible to defenders as well as attackers is half a control.
    /// </para>
    /// </returns>
    Task<Result<BlobContent>> ReadAsync(BlobPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a blob's metadata without transferring its content.
    /// </summary>
    /// <param name="path">The tenant-scoped path.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The descriptor, or the same not-found failure as <see cref="ReadAsync"/>.</returns>
    Task<Result<BlobDescriptor>> StatAsync(BlobPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a blob.
    /// </summary>
    /// <param name="path">The tenant-scoped path.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>
    /// Success, or the same not-found failure. Deleting another tenant's blob
    /// reports not-found **and does not delete it** — the check precedes the
    /// operation, not the response.
    /// </returns>
    Task<Result> DeleteAsync(BlobPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the blobs beneath a prefix within the current tenant.
    /// </summary>
    /// <param name="prefixSegments">
    /// Segments below the tenant root, validated exactly as
    /// <see cref="BlobPath.Create"/> validates them. Passing none lists the
    /// tenant root.
    /// </param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>
    /// Descriptors within the current tenant only. There is no parameter that
    /// widens this to another tenant, so a listing cannot be made to cross the
    /// boundary.
    /// </returns>
    Task<Result<IReadOnlyList<BlobDescriptor>>> ListAsync(
        IReadOnlyList<string> prefixSegments,
        CancellationToken cancellationToken);
}

/// <summary>
/// Raw storage I/O for one backing technology. Filesystem, SFTP, S3, Azure
/// Blob and the rest implement this, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// **A backend is deliberately not an <see cref="IBlobStore"/>.** It cannot be
/// registered in place of one, because it does not satisfy the interface —
/// which means the policy layer cannot be bypassed by a dependency-injection
/// mistake. That is a compile error rather than a code review.
/// </para>
/// <para>
/// Backends receive an already-validated <see cref="BlobPath"/> and are not
/// required to enforce tenancy. They must not weaken it either: the path they
/// are handed is tenant-prefixed by construction, so honouring it literally is
/// sufficient.
/// </para>
/// </remarks>
public interface IBlobBackend
{
    /// <summary>A stable name for diagnostics — <c>FileSystem</c>, <c>S3</c>, and so on.</summary>
    string BackendName { get; }

    /// <summary>
    /// Stores bytes at a path, replacing what is there.
    /// </summary>
    /// <param name="path">The already-validated destination.</param>
    /// <param name="bytes">The bytes to store — ciphertext when the policy encrypted them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or a provider failure.</returns>
    Task<Result> PutAsync(BlobPath path, byte[] bytes, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the stored bytes.
    /// </summary>
    /// <param name="path">The already-validated path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored bytes, or a not-found failure.</returns>
    Task<Result<byte[]>> GetAsync(BlobPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a blob.
    /// </summary>
    /// <param name="path">The already-validated path.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>Success, or a not-found failure.</returns>
    Task<Result> RemoveAsync(BlobPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates stored paths under a rendered prefix.
    /// </summary>
    /// <param name="renderedPrefix">The prefix, already tenant-rooted by the policy layer.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The matching full paths.</returns>
    Task<Result<IReadOnlyList<string>>> ListAsync(string renderedPrefix, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the sidecar metadata the policy layer stored alongside the blob.
    /// </summary>
    /// <param name="path">The already-validated path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The metadata as written, or a not-found failure.</returns>
    Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        BlobPath path,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes sidecar metadata alongside a blob.
    /// </summary>
    /// <param name="path">The already-validated path.</param>
    /// <param name="metadata">The metadata to persist verbatim.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or a provider failure.</returns>
    Task<Result> PutMetadataAsync(
        BlobPath path,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken);
}
