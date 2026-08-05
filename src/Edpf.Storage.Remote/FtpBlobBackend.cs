using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Core.Guards;

namespace Edpf.Storage.Remote;

/// <summary>
/// An <see cref="IBlobBackend"/> over FTP and FTPS.
/// </summary>
/// <remarks>
/// <para>
/// Written against the protocol rather than <c>FtpWebRequest</c>, which is
/// obsolete (SYSLIB0014) and would fail this repository's warnings-as-errors
/// build outright. The socket work lives behind <see cref="IFtpChannel"/> so
/// the parts that actually break — command sequencing, reply parsing, the
/// transfer mode — are tested without a server.
/// </para>
/// <para>
/// **Two rules here are correctness, not style.**
/// </para>
/// <list type="number">
///   <item>
///     <c>TYPE I</c> is sent before every transfer. In the default ASCII mode
///     an FTP server rewrites line endings *inside the payload*, which
///     silently corrupts every binary file and, worse, every encrypted one —
///     where the damage surfaces later as an authentication-tag failure that
///     looks like a crypto bug.
///   </item>
///   <item>
///     EPSV is tried before PASV. A 227 reply carries the server's own idea of
///     its address, which behind NAT is a private one the client cannot reach;
///     EPSV carries only a port and reuses the control connection's host.
///   </item>
/// </list>
/// <para>
/// **On plain FTP.** It authenticates in cleartext and transfers in cleartext.
/// A deployment handling anything above <c>Public</c> must use FTPS, and that
/// is a deployment decision this class cannot make — but
/// <see cref="RequireEncryptedChannel"/> exists so it can be made once, at
/// composition, rather than remembered at each call site.
/// </para>
/// </remarks>
public sealed class FtpBlobBackend : IBlobBackend
{
    private readonly IFtpChannel _channel;

    /// <summary>
    /// Composes the backend over a channel.
    /// </summary>
    /// <param name="channel">The control and data channel, already authenticated.</param>
    /// <param name="requireEncryptedChannel">
    /// Whether the channel must be TLS-protected. When true and the channel
    /// reports otherwise, every operation is refused rather than performed in
    /// the clear.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requireEncryptedChannel"/> is true and the channel is
    /// not TLS-protected. Refused at composition rather than per operation:
    /// a deployment that intended FTPS and got plain FTP should fail to start,
    /// not discover it after the first patient record has crossed the wire in
    /// the clear.
    /// </exception>
    public FtpBlobBackend(IFtpChannel channel, bool requireEncryptedChannel = true)
    {
        _channel = Guard.NotNull(channel, nameof(channel));
        RequireEncryptedChannel = requireEncryptedChannel;

        if (requireEncryptedChannel && !channel.IsEncrypted)
        {
            throw new ArgumentException(
                "This backend requires FTPS and the channel is not encrypted. Plain FTP authenticates and "
                + "transfers in cleartext; if that is genuinely intended, say so explicitly.",
                nameof(channel));
        }
    }

    /// <summary>Whether the channel must be TLS-protected.</summary>
    public bool RequireEncryptedChannel { get; }

    /// <summary>The sidecar file suffix. Reserved.</summary>
    internal const string MetadataSuffix = ".edpfmeta";

    private const char Separator = '\u001F';

    /// <inheritdoc />
    public string BackendName => "Ftp";

    /// <inheritdoc />
    public async Task<Result> PutAsync(BlobPath path, byte[] bytes, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(bytes, nameof(bytes));

        return await StoreAsync(path.Value, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> GetAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        return await RetrieveAsync(path.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> RemoveAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        FtpResponse deleted = await _channel
            .CommandAsync("DELE " + path.Value, cancellationToken)
            .ConfigureAwait(false);

        if (deleted.IsNotFound)
        {
            return Result.Failure(NotFound());
        }

        if (!deleted.IsPositive)
        {
            return Result.Failure(ProviderFailure());
        }

        await _channel
            .CommandAsync("DELE " + path.Value + MetadataSuffix, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> ListAsync(
        string renderedPrefix,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(renderedPrefix, nameof(renderedPrefix));

        Result<byte[]> listing = await TransferAsync(
            "NLST " + renderedPrefix.TrimEnd('/'), null, cancellationToken).ConfigureAwait(false);

        // A directory that is not there lists as nothing. "No documents yet"
        // and "the server is unreachable" must not collapse into one answer.
        if (listing.IsFailure)
        {
            return listing.Error!.Code == ErrorCodes.NotFound
                ? Result<IReadOnlyList<string>>.FromValue(Array.Empty<string>())
                : Result.Failure<IReadOnlyList<string>>(listing.Error!);
        }

        var names = new List<string>();
        foreach (string line in Encoding.UTF8.GetString(listing.Value).Split('\n'))
        {
            string name = line.Trim('\r', ' ');

            if (name.Length > 0 && !name.EndsWith(MetadataSuffix, StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        BlobPath path,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<byte[]> read = await RetrieveAsync(
            path.Value + MetadataSuffix, cancellationToken).ConfigureAwait(false);

        if (read.IsFailure)
        {
            return Result.Failure<IReadOnlyDictionary<string, string>>(read.Error!);
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Encoding.UTF8.GetString(read.Value).Split('\n'))
        {
            int separator = line.IndexOf(Separator, StringComparison.Ordinal);
            if (separator > 0)
            {
                metadata[line.Substring(0, separator)] = line.Substring(separator + 1).TrimEnd('\r');
            }
        }

        return metadata;
    }

    /// <inheritdoc />
    public async Task<Result> PutMetadataAsync(
        BlobPath path,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(metadata, nameof(metadata));

        var builder = new StringBuilder();
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            builder.Append(entry.Key).Append(Separator).Append(entry.Value).Append('\n');
        }

        return await StoreAsync(
            path.Value + MetadataSuffix,
            Encoding.UTF8.GetBytes(builder.ToString()),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> StoreAsync(string key, byte[] bytes, CancellationToken cancellationToken)
    {
        Result directories = await EnsureDirectoriesAsync(key, cancellationToken).ConfigureAwait(false);
        if (directories.IsFailure)
        {
            return directories;
        }

        Result<byte[]> stored = await TransferAsync("STOR " + key, bytes, cancellationToken)
            .ConfigureAwait(false);

        return stored.IsFailure ? Result.Failure(stored.Error!) : Result.Success();
    }

    private async Task<Result<byte[]>> RetrieveAsync(string key, CancellationToken cancellationToken)
        => await TransferAsync("RETR " + key, null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs one data transfer: set binary mode, negotiate passive mode, open
    /// the data connection, issue the command, move the bytes, read the
    /// completion reply.
    /// </summary>
    private async Task<Result<byte[]>> TransferAsync(
        string command,
        byte[]? upload,
        CancellationToken cancellationToken)
    {
        // TYPE I first, every time. In ASCII mode the server rewrites line
        // endings inside the payload — which corrupts binaries silently, and
        // corrupts ciphertext into a tag mismatch that reads like a crypto bug.
        FtpResponse binary = await _channel.CommandAsync("TYPE I", cancellationToken).ConfigureAwait(false);
        if (!binary.IsPositive)
        {
            return Result.Failure<byte[]>(ProviderFailure());
        }

        Result<FtpDataEndpoint> endpoint = await NegotiatePassiveAsync(cancellationToken).ConfigureAwait(false);
        if (endpoint.IsFailure)
        {
            return Result.Failure<byte[]>(endpoint.Error!);
        }

        Stream data = await _channel
            .OpenDataAsync(endpoint.Value, cancellationToken)
            .ConfigureAwait(false);

        FtpResponse started = await _channel.CommandAsync(command, cancellationToken).ConfigureAwait(false);
        if (started.IsNotFound)
        {
            return Result.Failure<byte[]>(NotFound());
        }

        if (!started.IsPositive)
        {
            return Result.Failure<byte[]>(ProviderFailure());
        }

        byte[] received;

        if (upload is not null)
        {
            await data.WriteAsync(upload.AsMemory(0, upload.Length), cancellationToken).ConfigureAwait(false);
            await data.FlushAsync(cancellationToken).ConfigureAwait(false);
            received = Array.Empty<byte>();
        }
        else
        {
            using var buffer = new MemoryStream();
            await data.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            received = buffer.ToArray();
        }

        await data.DisposeAsync().ConfigureAwait(false);

        // The transfer-complete reply arrives only after the data connection
        // closes. Skipping it leaves the control connection one reply out of
        // step, and the next command reads this one's answer.
        FtpResponse completed = await _channel.ReadReplyAsync(cancellationToken).ConfigureAwait(false);

        return completed.IsPositive
            ? received
            : Result.Failure<byte[]>(ProviderFailure());
    }

    private async Task<Result<FtpDataEndpoint>> NegotiatePassiveAsync(CancellationToken cancellationToken)
    {
        // EPSV first: a 227 reply carries the server's own address, which
        // behind NAT is one the client cannot reach.
        FtpResponse extended = await _channel.CommandAsync("EPSV", cancellationToken).ConfigureAwait(false);
        if (extended.Code == 229)
        {
            return FtpReply.ParseExtendedPassive(extended.Text);
        }

        FtpResponse passive = await _channel.CommandAsync("PASV", cancellationToken).ConfigureAwait(false);

        return passive.Code == 227
            ? FtpReply.ParsePassive(passive.Text)
            : Result.Failure<FtpDataEndpoint>(ProviderFailure());
    }

    private async Task<Result> EnsureDirectoriesAsync(string key, CancellationToken cancellationToken)
    {
        string[] segments = key.Split('/');
        var current = new StringBuilder();

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current.Length > 0)
            {
                current.Append('/');
            }

            current.Append(segments[i]);

            // 550 here means "it already exists", which is the common case.
            // MKD has no idempotent form, so the reply is the only signal.
            await _channel
                .CommandAsync("MKD " + current.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }

        return Result.Success();
    }

    private static Error NotFound() => new(
        ErrorCodes.NotFound,
        "The requested resource was not found.",
        ErrorCategory.NotFound);

    private static Error ProviderFailure() => new(
        ErrorCodes.ProviderFailure,
        "The storage backend could not complete the operation.",
        ErrorCategory.Transient);
}
