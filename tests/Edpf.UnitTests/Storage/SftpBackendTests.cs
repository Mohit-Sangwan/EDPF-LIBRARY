using System.Buffers.Binary;
using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Storage.Remote;

namespace Edpf.UnitTests.Storage;

/// <summary>
/// The SFTP backend. The protocol work is here and tested; only the SSH
/// transport is delegated, and <see cref="ISftpChannel"/> says why.
/// </summary>
public sealed class SftpBackendTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // ── wire format ───────────────────────────────────────────────────────

    [Fact]
    public void Frame_WritesABigEndianLengthCoveringTypeAndPayload()
    {
        // Little-endian here desynchronises the stream on the first packet,
        // and every reply afterwards is attributed to the wrong request — which
        // reads as data corruption rather than as a framing bug.
        byte[] framed = SftpWire.Frame(SftpPacketType.Open, [0xAA, 0xBB]);

        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(framed.AsSpan(0, 4)));
        Assert.Equal(SftpPacketType.Open, framed[4]);
        Assert.Equal<byte[]>([0xAA, 0xBB], framed[5..]);
    }

    [Fact]
    public void Text_IsLengthPrefixedUtf8()
    {
        byte[] encoded = SftpWire.Text("ward");

        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(0, 4)));
        Assert.Equal("ward", Encoding.UTF8.GetString(encoded, 4, 4));
    }

    [Fact]
    public void LongWord_IsBigEndian_BecauseOffsetsAre64Bit()
    {
        byte[] encoded = SftpWire.LongWord(0x0102030405060708);

        Assert.Equal<byte[]>([1, 2, 3, 4, 5, 6, 7, 8], encoded);
    }

    [Fact]
    public void ReadString_RefusesALengthLongerThanTheBuffer()
    {
        // A truncated or hostile packet. Refused rather than clamped: clamping
        // would return a prefix of somebody's filename and carry on as though
        // it had parsed.
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 9999);

        int offset = 0;
        Assert.Null(SftpWire.ReadString(payload, ref offset));
    }

    [Fact]
    public void ReadString_RoundTripsWhatTextWrote()
    {
        byte[] encoded = SftpWire.Text("tenants/a/report.pdf");

        int offset = 0;
        Assert.Equal("tenants/a/report.pdf", SftpWire.ReadString(encoded, ref offset));
        Assert.Equal(encoded.Length, offset);
    }

    // ── the backend ───────────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_OpensWritesAndClosesTheHandle()
    {
        var channel = new ScriptedChannel();
        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        Assert.True((await backend.PutAsync(
            BlobPath.Create(TenantA, "q3.bin"), [1, 2, 3], default)).IsSuccess);

        Assert.Contains(SftpPacketType.Open, channel.SentTypes);
        Assert.Contains(SftpPacketType.Write, channel.SentTypes);
        Assert.Contains(SftpPacketType.Close, channel.SentTypes);
    }

    [Fact]
    public async Task PutAsync_CreatesEveryAncestorDirectory()
    {
        var channel = new ScriptedChannel();
        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        await backend.PutAsync(BlobPath.Create(TenantA, "docs", "q3.bin"), [1], default);

        int made = channel.SentTypes.Count(t => t == SftpPacketType.MakeDirectory);

        // tenants, tenants/{guid}, tenants/{guid}/docs
        Assert.Equal(3, made);
    }

    [Fact]
    public async Task GetAsync_ReadsUntilTheServerReportsEndOfFile()
    {
        // A server is entitled to return fewer bytes than asked for. Treating a
        // short read as the end truncates the file, and the corruption only
        // shows up on large ones.
        var channel = new ScriptedChannel
        {
            DataChunks = [[1, 2], [3, 4], [5]],
        };

        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "q3.bin"), default);

        Assert.True(read.IsSuccess);
        Assert.Equal<byte[]>([1, 2, 3, 4, 5], read.Value);
    }

    [Fact]
    public async Task GetAsync_OfAMissingFile_IsNotFound()
    {
        var channel = new ScriptedChannel { OpenStatus = SftpStatus.NoSuchFile };
        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "missing.bin"), default);

        Assert.True(read.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, read.Error!.Code);
    }

    [Fact]
    public async Task GetAsync_OnPermissionDenied_AlsoPresentsAsNotFound()
    {
        // Matching the store's contract above: a refusal and an absence are
        // deliberately indistinguishable at this boundary.
        var channel = new ScriptedChannel { OpenStatus = SftpStatus.PermissionDenied };
        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "secret.bin"), default);

        Assert.Equal(ErrorCodes.NotFound, read.Error!.Code);
    }

    [Fact]
    public async Task GetAsync_OnAGenericFailure_IsATransientProviderFailure()
    {
        var channel = new ScriptedChannel { OpenStatus = SftpStatus.Failure };
        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "q3.bin"), default);

        Assert.Equal(ErrorCodes.ProviderFailure, read.Error!.Code);
        Assert.Equal(ErrorCategory.Transient, read.Error.Category);
    }

    [Fact]
    public async Task Exchange_ThatAnswersADifferentRequest_IsRefused()
    {
        // A mismatched request id means the stream is out of step. Continuing
        // would attribute this answer to the wrong question.
        var channel = new ScriptedChannel { ForceWrongRequestId = true };
        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "q3.bin"), default);

        Assert.True(read.IsFailure);
        Assert.Equal(ErrorCodes.IntegrationFailed, read.Error!.Code);
    }

    [Fact]
    public async Task ListAsync_PagesUntilEofAndExcludesDotEntriesAndSidecars()
    {
        var channel = new ScriptedChannel
        {
            DirectoryPages =
            [
                ["." , "..", "one.bin"],
                ["one.bin.edpfmeta", "two.bin"],
            ],
        };

        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/docs/", default);

        Assert.True(listed.IsSuccess);
        Assert.Equal(["tenants/a/docs/one.bin", "tenants/a/docs/two.bin"], listed.Value);
    }

    [Fact]
    public async Task ListAsync_OfAMissingDirectory_IsEmptyRatherThanAFailure()
    {
        var channel = new ScriptedChannel { OpenStatus = SftpStatus.NoSuchFile };
        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/nothing/", default);

        Assert.True(listed.IsSuccess);
        Assert.Empty(listed.Value);
    }

    [Fact]
    public async Task RemoveAsync_OfAnAbsentFile_IsNotFound()
    {
        var channel = new ScriptedChannel { RemoveStatus = SftpStatus.NoSuchFile };
        var backend = new SftpBlobBackend(channel, "/srv/edpf");

        Result removed = await backend.RemoveAsync(BlobPath.Create(TenantA, "missing.bin"), default);

        Assert.True(removed.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, removed.Error!.Code);
    }

    /// <summary>Answers SFTP packets from a script. No SSH, no sockets.</summary>
    private sealed class ScriptedChannel : ISftpChannel
    {
        private int _dataIndex;
        private int _directoryIndex;

        public List<byte> SentTypes { get; } = [];

        public uint OpenStatus { get; set; } = SftpStatus.Ok;

        public uint RemoveStatus { get; set; } = SftpStatus.Ok;

        public bool ForceWrongRequestId { get; set; }

        public List<byte[]> DataChunks { get; set; } = [];

        public List<string[]> DirectoryPages { get; set; } = [];

        public Task<Result<SftpReply>> ExchangeAsync(byte[] packet, CancellationToken cancellationToken)
        {
            byte type = packet[4];
            uint requestId = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(5, 4));

            SentTypes.Add(type);

            if (ForceWrongRequestId)
            {
                return Reply(new SftpReply(SftpPacketType.Status, requestId + 1, Status(SftpStatus.Ok)));
            }

            return type switch
            {
                SftpPacketType.Open or SftpPacketType.OpenDirectory => Reply(
                    OpenStatus == SftpStatus.Ok
                        ? new SftpReply(SftpPacketType.Handle, requestId, SftpWire.Text("h1"))
                        : new SftpReply(SftpPacketType.Status, requestId, Status(OpenStatus))),

                SftpPacketType.Read => Reply(NextData(requestId)),

                SftpPacketType.ReadDirectory => Reply(NextDirectoryPage(requestId)),

                SftpPacketType.Remove => Reply(
                    new SftpReply(SftpPacketType.Status, requestId, Status(RemoveStatus))),

                _ => Reply(new SftpReply(SftpPacketType.Status, requestId, Status(SftpStatus.Ok))),
            };
        }

        public void Dispose()
        {
        }

        private SftpReply NextData(uint requestId)
        {
            if (_dataIndex >= DataChunks.Count)
            {
                return new SftpReply(SftpPacketType.Status, requestId, Status(SftpStatus.Eof));
            }

            byte[] chunk = DataChunks[_dataIndex++];

            return new SftpReply(
                SftpPacketType.Data,
                requestId,
                SftpWire.Join(SftpWire.Word((uint)chunk.Length), chunk));
        }

        private SftpReply NextDirectoryPage(uint requestId)
        {
            if (_directoryIndex >= DirectoryPages.Count)
            {
                return new SftpReply(SftpPacketType.Status, requestId, Status(SftpStatus.Eof));
            }

            string[] names = DirectoryPages[_directoryIndex++];
            var parts = new List<byte[]> { SftpWire.Word((uint)names.Length) };

            foreach (string name in names)
            {
                parts.Add(SftpWire.Text(name));
                parts.Add(SftpWire.Text("-rw-r--r-- 1 owner group 0 Jan 1 00:00 " + name));
                parts.Add(SftpWire.Word(0));
            }

            return new SftpReply(SftpPacketType.Name, requestId, SftpWire.Join([.. parts]));
        }

        private static byte[] Status(uint code) => SftpWire.Word(code);

        private static Task<Result<SftpReply>> Reply(SftpReply reply)
            => Task.FromResult(Result<SftpReply>.FromValue(reply));
    }
}
