using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Core.Guards;

namespace Edpf.Storage.Remote;

/// <summary>SFTP packet types used by this client (draft-ietf-secsh-filexfer-02).</summary>
public static class SftpPacketType
{
    /// <summary>Client hello, carrying the protocol version.</summary>
    public const byte Init = 1;

    /// <summary>Server hello.</summary>
    public const byte Version = 2;

    /// <summary>Open a file.</summary>
    public const byte Open = 3;

    /// <summary>Close a handle.</summary>
    public const byte Close = 4;

    /// <summary>Read from a handle.</summary>
    public const byte Read = 5;

    /// <summary>Write to a handle.</summary>
    public const byte Write = 6;

    /// <summary>Remove a file.</summary>
    public const byte Remove = 13;

    /// <summary>Create a directory.</summary>
    public const byte MakeDirectory = 14;

    /// <summary>Open a directory for listing.</summary>
    public const byte OpenDirectory = 11;

    /// <summary>Read directory entries.</summary>
    public const byte ReadDirectory = 12;

    /// <summary>A status reply.</summary>
    public const byte Status = 101;

    /// <summary>A handle reply.</summary>
    public const byte Handle = 102;

    /// <summary>A data reply.</summary>
    public const byte Data = 103;

    /// <summary>A name-list reply.</summary>
    public const byte Name = 104;
}

/// <summary>SFTP status codes.</summary>
public static class SftpStatus
{
    /// <summary>Success.</summary>
    public const uint Ok = 0;

    /// <summary>End of file, or end of a directory listing.</summary>
    public const uint Eof = 1;

    /// <summary>No such file.</summary>
    public const uint NoSuchFile = 2;

    /// <summary>Permission denied.</summary>
    public const uint PermissionDenied = 3;

    /// <summary>Generic failure — includes "directory already exists".</summary>
    public const uint Failure = 4;
}

/// <summary>
/// Reads and writes SFTP packets.
/// </summary>
/// <remarks>
/// <para>
/// The framing is the part worth implementing and testing directly: a 32-bit
/// big-endian length, a one-byte type, a 32-bit request id, then type-specific
/// fields where every string is itself length-prefixed. Getting the endianness
/// or a length wrong desynchronises the stream, and every subsequent reply is
/// then attributed to the wrong request — which reads like data corruption
/// rather than a framing bug.
/// </para>
/// </remarks>
public static class SftpWire
{
    /// <summary>Builds a packet body with a length prefix and a type byte.</summary>
    /// <param name="type">The packet type.</param>
    /// <param name="payload">Everything after the type byte.</param>
    /// <returns>The framed packet.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public static byte[] Frame(byte type, byte[] payload)
    {
        Guard.NotNull(payload, nameof(payload));

        var packet = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), (uint)(1 + payload.Length));
        packet[4] = type;
        Array.Copy(payload, 0, packet, 5, payload.Length);

        return packet;
    }

    /// <summary>Encodes a length-prefixed UTF-8 string.</summary>
    /// <param name="value">The string.</param>
    /// <returns>The encoded bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static byte[] Text(string value)
    {
        Guard.NotNull(value, nameof(value));

        byte[] text = Encoding.UTF8.GetBytes(value);
        var encoded = new byte[4 + text.Length];

        BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(0, 4), (uint)text.Length);
        Array.Copy(text, 0, encoded, 4, text.Length);

        return encoded;
    }

    /// <summary>Encodes a 32-bit big-endian value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Four bytes.</returns>
    public static byte[] Word(uint value)
    {
        var encoded = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, value);
        return encoded;
    }

    /// <summary>Encodes a 64-bit big-endian value — file offsets are 64-bit.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Eight bytes.</returns>
    public static byte[] LongWord(ulong value)
    {
        var encoded = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(encoded, value);
        return encoded;
    }

    /// <summary>Concatenates encoded fields.</summary>
    /// <param name="parts">The fields, in order.</param>
    /// <returns>The joined payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parts"/> is null.</exception>
    public static byte[] Join(params byte[][] parts)
    {
        Guard.NotNull(parts, nameof(parts));

        int length = 0;
        foreach (byte[] part in parts)
        {
            length += part.Length;
        }

        var joined = new byte[length];
        int offset = 0;

        foreach (byte[] part in parts)
        {
            Array.Copy(part, 0, joined, offset, part.Length);
            offset += part.Length;
        }

        return joined;
    }

    /// <summary>Reads a length-prefixed string from a payload.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="offset">Where to start; advanced past the string.</param>
    /// <returns>The string, or null when the payload is truncated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public static string? ReadString(byte[] payload, ref int offset)
    {
        Guard.NotNull(payload, nameof(payload));

        if (offset + 4 > payload.Length)
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset, 4));
        offset += 4;

        // A declared length longer than the buffer is a truncated or hostile
        // packet. Refused rather than clamped: clamping would return a prefix
        // of somebody's filename and carry on as though it parsed.
        if (length > int.MaxValue || offset + (int)length > payload.Length)
        {
            return null;
        }

        string value = Encoding.UTF8.GetString(payload, offset, (int)length);
        offset += (int)length;

        return value;
    }

    /// <summary>Reads a 32-bit big-endian value.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="offset">Where to start; advanced past the value.</param>
    /// <returns>The value, or null when the payload is truncated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public static uint? ReadUInt32(byte[] payload, ref int offset)
    {
        Guard.NotNull(payload, nameof(payload));

        if (offset + 4 > payload.Length)
        {
            return null;
        }

        uint value = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset, 4));
        offset += 4;

        return value;
    }
}

/// <summary>One SFTP reply: its type, request id and payload.</summary>
public sealed class SftpReply
{
    /// <summary>Records a reply.</summary>
    /// <param name="type">The packet type.</param>
    /// <param name="requestId">The request it answers.</param>
    /// <param name="payload">Everything after the request id.</param>
    public SftpReply(byte type, uint requestId, byte[] payload)
    {
        Type = type;
        RequestId = requestId;
        Payload = payload;
    }

    /// <summary>The packet type.</summary>
    public byte Type { get; }

    /// <summary>The request it answers.</summary>
    public uint RequestId { get; }

#pragma warning disable CA1819 // The payload IS the packet; copying per access is not the trade here.
    /// <summary>Everything after the request id.</summary>
    public byte[] Payload { get; }
#pragma warning restore CA1819
}

/// <summary>
/// An open SSH connection with the SFTP subsystem started.
/// </summary>
/// <remarks>
/// <para>
/// **The seam exists because hand-writing SSH would be the wrong call, and
/// saying so is more useful than pretending otherwise.** The transport needs
/// key exchange, host-key verification, cipher and MAC negotiation, and the
/// channel layer — thousands of lines of security-critical code, and it would
/// mean implementing Diffie-Hellman, curve25519 and AES-CTR by hand in a
/// repository whose Z.10 rule is that framework code never touches
/// <c>System.Security.Cryptography</c> directly. Code I wrote unreviewed would
/// be worse than a maintained library, and worse in the one place where
/// "worse" means "exploitable".
/// </para>
/// <para>
/// So the split is: **the SFTP protocol is implemented and tested here**, and
/// the SSH transport is a small adapter a deployment supplies — around SSH.NET
/// in an optional package, or around a platform SSH agent. The adapter is
/// roughly thirty lines against an existing library; everything above it, and
/// every control the store applies, is already written and covered.
/// </para>
/// </remarks>
public interface ISftpTransport : IDisposable
{
    /// <summary>Sends a framed packet and reads the reply that answers it.</summary>
    /// <param name="packet">The framed packet.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The reply.</returns>
    Task<Result<SftpReply>> ExchangeAsync(byte[] packet, CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="IBlobBackend"/> over SFTP.
/// </summary>
/// <remarks>
/// The protocol work — packet framing, handle lifecycle, chunked reads to end
/// of file, status-code mapping — lives here and is tested against a scripted
/// channel. Only the SSH transport is delegated; see
/// <see cref="ISftpTransport"/> for why.
/// </remarks>
public sealed class SftpBlobBackend : IBlobBackend, IChunkedUploadBackend
{
    /// <summary>Open for reading.</summary>
    private const uint FlagRead = 0x00000001;

    /// <summary>Open for writing, creating and truncating.</summary>
    private const uint FlagWriteCreateTruncate = 0x00000002 | 0x00000008 | 0x00000010;

    /// <summary>No attributes supplied.</summary>
    private const uint NoAttributes = 0;

    private const int ReadChunkSize = 32_768;

    private readonly ISftpTransport _channel;
    private readonly string _root;
    private uint _requestId;

    /// <summary>
    /// Composes the backend.
    /// </summary>
    /// <param name="channel">An open channel with the SFTP subsystem started.</param>
    /// <param name="rootPath">The remote directory everything is stored under.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> is blank.</exception>
    public SftpBlobBackend(ISftpTransport channel, string rootPath)
    {
        _channel = Guard.NotNull(channel, nameof(channel));
        _root = Guard.NotNullOrWhiteSpace(rootPath, nameof(rootPath)).TrimEnd('/');
    }

    /// <summary>The sidecar file suffix. Reserved.</summary>
    internal const string MetadataSuffix = ".edpfmeta";

    private const char Separator = '\u001F';

    /// <inheritdoc />
    public string BackendName => "Sftp";

    /// <inheritdoc />
    public async Task<Result> PutAsync(BlobPath path, byte[] bytes, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(bytes, nameof(bytes));

        return await WriteFileAsync(path.Value, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> GetAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        return await ReadFileAsync(path.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> RemoveAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result removed = await RemoveFileAsync(path.Value, cancellationToken).ConfigureAwait(false);
        if (removed.IsFailure)
        {
            return removed;
        }

        // Best effort: a blob with no sidecar is already handled as unreadable
        // by the layer above, so a failure here cannot make things worse.
        await RemoveFileAsync(path.Value + MetadataSuffix, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> ListAsync(
        string renderedPrefix,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(renderedPrefix, nameof(renderedPrefix));

        string directory = _root + "/" + renderedPrefix.TrimEnd('/');

        Result<SftpReply> opened = await SendAsync(
            SftpPacketType.OpenDirectory,
            SftpWire.Text(directory),
            cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return Result.Failure<IReadOnlyList<string>>(opened.Error!);
        }

        if (opened.Value.Type == SftpPacketType.Status)
        {
            // A directory that is not there lists as nothing, so "no documents
            // yet" stays distinguishable from "the server is unreachable".
            return StatusOf(opened.Value) == SftpStatus.NoSuchFile
                ? Result<IReadOnlyList<string>>.FromValue(Array.Empty<string>())
                : Result.Failure<IReadOnlyList<string>>(ProviderFailure());
        }

        string? handle = HandleOf(opened.Value);
        if (handle is null)
        {
            return Result.Failure<IReadOnlyList<string>>(Malformed());
        }

        var names = new List<string>();

        try
        {
            while (true)
            {
                Result<SftpReply> page = await SendAsync(
                    SftpPacketType.ReadDirectory,
                    SftpWire.Text(handle),
                    cancellationToken).ConfigureAwait(false);

                if (page.IsFailure)
                {
                    return Result.Failure<IReadOnlyList<string>>(page.Error!);
                }

                // A directory listing arrives in pages and ends with an EOF
                // status. Stopping at the first page would silently truncate
                // the listing, and a lifecycle sweep would then act on it.
                if (page.Value.Type == SftpPacketType.Status)
                {
                    break;
                }

                if (!AppendNames(page.Value, renderedPrefix, names))
                {
                    return Result.Failure<IReadOnlyList<string>>(Malformed());
                }
            }
        }
        finally
        {
            await SendAsync(SftpPacketType.Close, SftpWire.Text(handle), cancellationToken)
                .ConfigureAwait(false);
        }

        return names;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        BlobPath path,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<byte[]> read = await ReadFileAsync(
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
                metadata[line.Substring(0, separator)] = line.Substring(separator + 1);
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

        return await WriteFileAsync(
            path.Value + MetadataSuffix,
            Encoding.UTF8.GetBytes(builder.ToString()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<string>> BeginChunkedAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        // SFTP has no upload-session concept: a resumable write is just an
        // open handle plus an offset. The handle IS the upload id, which is
        // why this returns one rather than inventing a correlation table.
        Result directories = await EnsureDirectoriesAsync(path.Value, cancellationToken).ConfigureAwait(false);
        if (directories.IsFailure)
        {
            return Result.Failure<string>(directories.Error!);
        }

        Result<SftpReply> opened = await SendAsync(
            SftpPacketType.Open,
            SftpWire.Join(
                SftpWire.Text(_root + "/" + path.Value),
                SftpWire.Word(FlagWriteCreateTruncate),
                SftpWire.Word(NoAttributes)),
            cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return Result.Failure<string>(opened.Error!);
        }

        string? handle = HandleOf(opened.Value);

        return handle is null
            ? Result.Failure<string>(MapStatus(StatusOf(opened.Value)))
            : handle;
    }

    /// <inheritdoc />
    public async Task<Result<string>> AppendChunkAsync(
        BlobPath path,
        string uploadId,
        int partNumber,
        long offset,
        byte[] chunk,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNullOrWhiteSpace(uploadId, nameof(uploadId));
        Guard.NotNull(chunk, nameof(chunk));

        // The OFFSET places the bytes here, not the part number. That is the
        // opposite of S3 and is what makes an SFTP upload genuinely resumable:
        // a client that reconnects seeks to where it stopped.
        Result<SftpReply> written = await SendAsync(
            SftpPacketType.Write,
            SftpWire.Join(
                SftpWire.Text(uploadId),
                SftpWire.LongWord((ulong)offset),
                SftpWire.Word((uint)chunk.Length),
                chunk),
            cancellationToken).ConfigureAwait(false);

        if (written.IsFailure)
        {
            return Result.Failure<string>(written.Error!);
        }

        return StatusOf(written.Value) == SftpStatus.Ok
            ? string.Empty
            : Result.Failure<string>(ProviderFailure());
    }

    /// <inheritdoc />
    public async Task<Result> CompleteChunkedAsync(
        BlobPath path,
        string uploadId,
        IReadOnlyList<string> partTags,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNullOrWhiteSpace(uploadId, nameof(uploadId));

        // Nothing to assemble: the offsets already put every byte in place.
        // Closing the handle is what makes the file durable.
        Result<SftpReply> closed = await SendAsync(
            SftpPacketType.Close, SftpWire.Text(uploadId), cancellationToken).ConfigureAwait(false);

        return closed.IsFailure ? Result.Failure(closed.Error!) : Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> AbortChunkedAsync(
        BlobPath path,
        string uploadId,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNullOrWhiteSpace(uploadId, nameof(uploadId));

        await SendAsync(SftpPacketType.Close, SftpWire.Text(uploadId), cancellationToken)
            .ConfigureAwait(false);

        // The partial file is removed. Leaving it would present a truncated
        // document as a complete one, which is worse than no document.
        return await RemoveFileAsync(path.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> WriteFileAsync(string key, byte[] bytes, CancellationToken cancellationToken)
    {
        Result directories = await EnsureDirectoriesAsync(key, cancellationToken).ConfigureAwait(false);
        if (directories.IsFailure)
        {
            return directories;
        }

        Result<SftpReply> opened = await SendAsync(
            SftpPacketType.Open,
            SftpWire.Join(
                SftpWire.Text(_root + "/" + key),
                SftpWire.Word(FlagWriteCreateTruncate),
                SftpWire.Word(NoAttributes)),
            cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return Result.Failure(opened.Error!);
        }

        string? handle = HandleOf(opened.Value);
        if (handle is null)
        {
            return Result.Failure(opened.Value.Type == SftpPacketType.Status
                ? MapStatus(StatusOf(opened.Value))
                : Malformed());
        }

        try
        {
            Result<SftpReply> written = await SendAsync(
                SftpPacketType.Write,
                SftpWire.Join(
                    SftpWire.Text(handle),
                    SftpWire.LongWord(0),
                    SftpWire.Word((uint)bytes.Length),
                    bytes),
                cancellationToken).ConfigureAwait(false);

            if (written.IsFailure)
            {
                return Result.Failure(written.Error!);
            }

            return StatusOf(written.Value) == SftpStatus.Ok
                ? Result.Success()
                : Result.Failure(ProviderFailure());
        }
        finally
        {
            await SendAsync(SftpPacketType.Close, SftpWire.Text(handle), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<Result<byte[]>> ReadFileAsync(string key, CancellationToken cancellationToken)
    {
        Result<SftpReply> opened = await SendAsync(
            SftpPacketType.Open,
            SftpWire.Join(
                SftpWire.Text(_root + "/" + key),
                SftpWire.Word(FlagRead),
                SftpWire.Word(NoAttributes)),
            cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return Result.Failure<byte[]>(opened.Error!);
        }

        string? handle = HandleOf(opened.Value);
        if (handle is null)
        {
            return Result.Failure<byte[]>(opened.Value.Type == SftpPacketType.Status
                ? MapStatus(StatusOf(opened.Value))
                : Malformed());
        }

        using var buffer = new MemoryStream();
        ulong offset = 0;

        try
        {
            while (true)
            {
                Result<SftpReply> chunk = await SendAsync(
                    SftpPacketType.Read,
                    SftpWire.Join(
                        SftpWire.Text(handle),
                        SftpWire.LongWord(offset),
                        SftpWire.Word(ReadChunkSize)),
                    cancellationToken).ConfigureAwait(false);

                if (chunk.IsFailure)
                {
                    return Result.Failure<byte[]>(chunk.Error!);
                }

                // Reads run to an explicit EOF status rather than to a short
                // read. A server is entitled to return fewer bytes than asked
                // for, and treating that as the end truncates the file.
                if (chunk.Value.Type == SftpPacketType.Status)
                {
                    break;
                }

                int position = 0;
                uint? length = SftpWire.ReadUInt32(chunk.Value.Payload, ref position);

                if (length is null || position + (int)length.Value > chunk.Value.Payload.Length)
                {
                    return Result.Failure<byte[]>(Malformed());
                }

                await buffer.WriteAsync(
                    chunk.Value.Payload.AsMemory(position, (int)length.Value), cancellationToken)
                    .ConfigureAwait(false);
                offset += length.Value;
            }
        }
        finally
        {
            await SendAsync(SftpPacketType.Close, SftpWire.Text(handle), cancellationToken)
                .ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private async Task<Result> RemoveFileAsync(string key, CancellationToken cancellationToken)
    {
        Result<SftpReply> removed = await SendAsync(
            SftpPacketType.Remove,
            SftpWire.Text(_root + "/" + key),
            cancellationToken).ConfigureAwait(false);

        if (removed.IsFailure)
        {
            return Result.Failure(removed.Error!);
        }

        uint status = StatusOf(removed.Value);

        return status == SftpStatus.Ok ? Result.Success() : Result.Failure(MapStatus(status));
    }

    private async Task<Result> EnsureDirectoriesAsync(string key, CancellationToken cancellationToken)
    {
        string[] segments = key.Split('/');
        var current = new StringBuilder(_root);

        for (int i = 0; i < segments.Length - 1; i++)
        {
            current.Append('/').Append(segments[i]);

            // A failure here is usually "it already exists", which SFTP reports
            // as a generic failure rather than a distinct code. Ignored for
            // that reason; a genuine permission problem surfaces on the open.
            await SendAsync(
                SftpPacketType.MakeDirectory,
                SftpWire.Join(SftpWire.Text(current.ToString()), SftpWire.Word(NoAttributes)),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    private static bool AppendNames(SftpReply reply, string prefix, List<string> into)
    {
        int position = 0;
        uint? count = SftpWire.ReadUInt32(reply.Payload, ref position);

        if (count is null)
        {
            return false;
        }

        for (uint i = 0; i < count.Value; i++)
        {
            string? name = SftpWire.ReadString(reply.Payload, ref position);
            string? longName = SftpWire.ReadString(reply.Payload, ref position);

            if (name is null || longName is null)
            {
                return false;
            }

            // Attributes follow each entry and are skipped: this client needs
            // names only, and parsing an optional attribute block it does not
            // use would be surface area for no benefit.
            uint? attributeFlags = SftpWire.ReadUInt32(reply.Payload, ref position);
            if (attributeFlags is null)
            {
                return false;
            }

            if (name is "." or ".." || name.EndsWith(MetadataSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            into.Add(prefix.TrimEnd('/') + "/" + name);
        }

        return true;
    }

    private async Task<Result<SftpReply>> SendAsync(
        byte type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        uint id = Interlocked.Increment(ref _requestId);

        byte[] packet = SftpWire.Frame(type, SftpWire.Join(SftpWire.Word(id), payload));

        Result<SftpReply> reply = await _channel.ExchangeAsync(packet, cancellationToken).ConfigureAwait(false);

        if (reply.IsSuccess && reply.Value.RequestId != id)
        {
            // A reply carrying another request's id means the stream has
            // desynchronised. Continuing would attribute this answer to the
            // wrong question, which reads as data corruption rather than as a
            // framing bug.
            return Result.Failure<SftpReply>(new Error(
                ErrorCodes.IntegrationFailed,
                "The SFTP server replied to a different request; the channel is out of step.",
                ErrorCategory.Integration));
        }

        return reply;
    }

    private static string? HandleOf(SftpReply reply)
    {
        if (reply.Type != SftpPacketType.Handle)
        {
            return null;
        }

        int position = 0;
        return SftpWire.ReadString(reply.Payload, ref position);
    }

    private static uint StatusOf(SftpReply reply)
    {
        if (reply.Type != SftpPacketType.Status)
        {
            return SftpStatus.Failure;
        }

        int position = 0;
        return SftpWire.ReadUInt32(reply.Payload, ref position) ?? SftpStatus.Failure;
    }

    private static Error MapStatus(uint status) => status switch
    {
        SftpStatus.NoSuchFile => NotFound(),

        // Permission denied presents as not-found, matching the store's
        // contract above: a refusal and an absence are deliberately
        // indistinguishable at this boundary.
        SftpStatus.PermissionDenied => NotFound(),

        _ => ProviderFailure(),
    };

    private static Error NotFound() => new(
        ErrorCodes.NotFound,
        "The requested resource was not found.",
        ErrorCategory.NotFound);

    private static Error ProviderFailure() => new(
        ErrorCodes.ProviderFailure,
        "The storage backend could not complete the operation.",
        ErrorCategory.Transient);

    private static Error Malformed() => new(
        ErrorCodes.SchemaMismatch,
        "The SFTP server sent a packet this client could not read.",
        ErrorCategory.Integration);
}
