using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Connectors;

/// <summary>How a connector authenticates to its source (Phase 26f).</summary>
public enum ConnectorAuthKind
{
    /// <summary>No authentication — a public feed.</summary>
    None = 0,

    /// <summary>An API key presented in a header.</summary>
    ApiKey = 1,

    /// <summary>HTTP basic.</summary>
    Basic = 2,

    /// <summary>OAuth 2 client credentials.</summary>
    OAuthClientCredentials = 3,

    /// <summary>A mutual-TLS client certificate.</summary>
    ClientCertificate = 4,
}

/// <summary>
/// What a connector needs, declared rather than coded (Phase 26f).
/// </summary>
/// <remarks>
/// <para>
/// The manifest is what makes an integration "configuration plus a thin
/// adapter rather than a bespoke project". It travels in source control, gets
/// reviewed, diffed, and copied between environments.
/// </para>
/// <para>
/// **Which is exactly why it names secrets rather than carrying them.** A
/// manifest holds an <see cref="Abstractions.Configuration.ISecretStore"/>
/// key; the value is fetched at
/// run time and never lands in a file, a diff, a container image, or a
/// support bundle. There is no property on this type that could hold a
/// credential, and an architecture test asserts that no such property appears
/// later.
/// </para>
/// </remarks>
public sealed class ConnectorManifest
{
    /// <summary>Initializes a manifest.</summary>
    /// <param name="name">The connector name.</param>
    /// <param name="sourceSystem">The system it integrates with.</param>
    /// <param name="authKind">How it authenticates.</param>
    /// <param name="credentialSecretName">
    /// The <c>ISecretStore</c> key holding the credential — a name, never a
    /// value.
    /// </param>
    /// <param name="pagination">How it pages.</param>
    /// <param name="retry">How it retries.</param>
    /// <param name="safetyLag">How far behind the source's clock it reads.</param>
    /// <exception cref="ArgumentException">Authentication is required but no secret is named.</exception>
    public ConnectorManifest(
        string name,
        string sourceSystem,
        ConnectorAuthKind authKind,
        string? credentialSecretName,
        PaginationPlan pagination,
        RetryPolicy retry,
        TimeSpan safetyLag)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        SourceSystem = Guard.NotNullOrWhiteSpace(sourceSystem, nameof(sourceSystem));
        AuthKind = authKind;
        CredentialSecretName = credentialSecretName;
        Pagination = Guard.NotNull(pagination, nameof(pagination));
        Retry = Guard.NotNull(retry, nameof(retry));
        SafetyLag = safetyLag;

        if (authKind != ConnectorAuthKind.None && string.IsNullOrWhiteSpace(credentialSecretName))
        {
            throw new ArgumentException(
                $"A connector using {authKind} must name the secret holding its credential. Leaving it "
                + "unset is how a credential ends up inline in the manifest instead.",
                nameof(credentialSecretName));
        }

        if (safetyLag <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The safety lag must be positive; see WatermarkPlanner for why reading up to the "
                + "source's current time loses records permanently.",
                nameof(safetyLag));
        }
    }

    /// <summary>The connector name.</summary>
    public string Name { get; }

    /// <summary>The system it integrates with.</summary>
    public string SourceSystem { get; }

    /// <summary>How it authenticates.</summary>
    public ConnectorAuthKind AuthKind { get; }

    /// <summary>
    /// The <c>ISecretStore</c> key holding the credential.
    /// </summary>
    /// <remarks>
    /// **A name, never a value.** The manifest is reviewed, diffed and copied
    /// between environments; a credential in it is a credential in every one
    /// of those places.
    /// </remarks>
    public string? CredentialSecretName { get; }

    /// <summary>How it pages.</summary>
    public PaginationPlan Pagination { get; }

    /// <summary>How it retries.</summary>
    public RetryPolicy Retry { get; }

    /// <summary>How far behind the source's clock it reads.</summary>
    public TimeSpan SafetyLag { get; }
}

/// <summary>
/// What one connector pass did (Phase 26f — connector-level audit).
/// </summary>
/// <remarks>
/// A sync that reports only "succeeded" is unfalsifiable. The record has to
/// carry enough to answer the question that actually gets asked — *"is the
/// data we are missing something the connector skipped?"* — which means the
/// window, the cursor movement and the counts.
/// </remarks>
public sealed class ConnectorRunRecord
{
    /// <summary>Initializes a run record.</summary>
    /// <param name="connectorName">Which connector.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="startedUtc">When the pass began.</param>
    /// <param name="window">The window it read.</param>
    /// <param name="cursorAfter">Where it finished.</param>
    /// <param name="recordsRead">How many records it read.</param>
    /// <param name="recordsRejected">How many the window checks refused.</param>
    /// <param name="attempts">How many HTTP attempts it made, retries included.</param>
    public ConnectorRunRecord(
        string connectorName,
        Guid tenantId,
        DateTimeOffset startedUtc,
        SyncWindow window,
        SyncCursor cursorAfter,
        int recordsRead,
        int recordsRejected,
        int attempts)
    {
        ConnectorName = Guard.NotNullOrWhiteSpace(connectorName, nameof(connectorName));
        TenantId = Guard.NotDefault(tenantId, nameof(tenantId));
        StartedUtc = startedUtc;
        Window = Guard.NotNull(window, nameof(window));
        CursorAfter = Guard.NotNull(cursorAfter, nameof(cursorAfter));
        RecordsRead = recordsRead;
        RecordsRejected = recordsRejected;
        Attempts = attempts;
    }

    /// <summary>Which connector.</summary>
    public string ConnectorName { get; }

    /// <summary>The owning tenant.</summary>
    public Guid TenantId { get; }

    /// <summary>When the pass began.</summary>
    public DateTimeOffset StartedUtc { get; }

    /// <summary>The window it read.</summary>
    public SyncWindow Window { get; }

    /// <summary>Where it finished.</summary>
    public SyncCursor CursorAfter { get; }

    /// <summary>How many records it read.</summary>
    public int RecordsRead { get; }

    /// <summary>
    /// How many records the window checks refused.
    /// </summary>
    /// <remarks>
    /// A non-zero count means the source is not honouring the bounds it was
    /// given, which is worth an alert rather than a log line: everything the
    /// framework's completeness argument rests on assumes the source filters
    /// as asked.
    /// </remarks>
    public int RecordsRejected { get; }

    /// <summary>How many HTTP attempts it made, retries included.</summary>
    public int Attempts { get; }

    /// <summary>
    /// A one-line summary for an operator, carrying no record content.
    /// </summary>
    /// <returns>The summary.</returns>
    /// <remarks>
    /// Names counts and positions only. A connector log that echoed record
    /// content would put the source's data — classified or not — into a log
    /// sink that was never assessed to hold it.
    /// </remarks>
    public override string ToString()
        => $"{ConnectorName}: read {RecordsRead}, rejected {RecordsRejected}, "
            + $"{Attempts} attempt(s), cursor now {CursorAfter}";
}
