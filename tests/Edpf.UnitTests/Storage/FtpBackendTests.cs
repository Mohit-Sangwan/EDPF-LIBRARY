using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Storage.Remote;

namespace Edpf.UnitTests.Storage;

/// <summary>
/// The FTP/FTPS backend. The socket work sits behind <see cref="IFtpChannel"/>
/// so the parts that actually break — reply parsing, command sequencing, the
/// transfer mode — are exercised without a server.
/// </summary>
public sealed class FtpBackendTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // ── reply parsing ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("220 Service ready", true)]
    [InlineData("220-Banner line one", false)]
    [InlineData("227 Entering Passive Mode (10,0,0,1,4,1)", true)]
    public void IsFinalLine_DistinguishesContinuationFromCompletion(string line, bool expected)
    {
        // A banner is routinely multi-line. A client that reads exactly one
        // line starts the session one reply out of step and misattributes
        // every response after it.
        Assert.Equal(expected, FtpReply.IsFinalLine(line));
    }

    [Fact]
    public void ParsePassive_ComputesThePortAsHighByteTimes256PlusLow()
    {
        // The single most common FTP client bug, and it hides: reversing the
        // bytes still works whenever they happen to be equal, so it passes
        // against a server on a convenient port and fails in production.
        Result<FtpDataEndpoint> endpoint =
            FtpReply.ParsePassive("227 Entering Passive Mode (10,0,0,1,195,80)");

        Assert.True(endpoint.IsSuccess);
        Assert.Equal("10.0.0.1", endpoint.Value.Host);
        Assert.Equal((195 * 256) + 80, endpoint.Value.Port);
        Assert.Equal(50000, endpoint.Value.Port);
    }

    [Theory]
    [InlineData("227 no parentheses")]
    [InlineData("227 (10,0,0,1,195)")]
    [InlineData("227 (10,0,0,1,195,999)")]
    [InlineData("227 (a,b,c,d,e,f)")]
    public void ParsePassive_RefusesAMalformedReply(string reply)
    {
        // Refused rather than guessed. A wrong endpoint connects somewhere,
        // and "somewhere" is not a good place to send a patient record.
        Assert.True(FtpReply.ParsePassive(reply).IsFailure);
    }

    [Fact]
    public void ParseExtendedPassive_ReadsThePortAndReusesTheControlHost()
    {
        // EPSV carries no address, which is exactly why it is preferred: a 227
        // reply behind NAT names the server's own private address.
        Result<FtpDataEndpoint> endpoint =
            FtpReply.ParseExtendedPassive("229 Entering Extended Passive Mode (|||50000|)");

        Assert.True(endpoint.IsSuccess);
        Assert.Null(endpoint.Value.Host);
        Assert.Equal(50000, endpoint.Value.Port);
    }

    [Fact]
    public void CodeOf_ReadsTheThreeDigitReplyCode()
    {
        Assert.Equal(550, FtpReply.CodeOf("550 File not found"));
        Assert.Equal(0, FtpReply.CodeOf("xx"));
    }

    // ── command sequencing ────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_SetsBinaryModeBeforeEveryTransfer()
    {
        // In ASCII mode the server rewrites line endings INSIDE the payload.
        // That corrupts binaries silently, and corrupts ciphertext into an
        // authentication-tag failure that reads like a crypto bug.
        var channel = new ScriptedChannel();
        var backend = new FtpBlobBackend(channel);

        await backend.PutAsync(BlobPath.Create(TenantA, "q3.bin"), [1, 2, 3], default);

        int typeIndex = channel.Commands.FindIndex(c => c == "TYPE I");
        int storIndex = channel.Commands.FindIndex(c => c.StartsWith("STOR", StringComparison.Ordinal));

        Assert.True(typeIndex >= 0, "TYPE I was never sent.");
        Assert.True(typeIndex < storIndex, "TYPE I must precede the transfer.");
    }

    [Fact]
    public async Task Transfer_PrefersEpsvAndFallsBackToPasv()
    {
        var channel = new ScriptedChannel();
        var backend = new FtpBlobBackend(channel);

        await backend.GetAsync(BlobPath.Create(TenantA, "q3.bin"), default);

        Assert.Contains("EPSV", channel.Commands);
        Assert.DoesNotContain("PASV", channel.Commands);
    }

    [Fact]
    public async Task Transfer_UsesPasvWhenEpsvIsNotUnderstood()
    {
        // Older servers answer 500. Falling back is what keeps this usable
        // against the appliance in the basement that nobody will upgrade.
        var channel = new ScriptedChannel { EpsvSupported = false };
        var backend = new FtpBlobBackend(channel);

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "q3.bin"), default);

        Assert.Contains("EPSV", channel.Commands);
        Assert.Contains("PASV", channel.Commands);
        Assert.True(read.IsSuccess);
    }

    [Fact]
    public async Task Transfer_ReadsTheCompletionReplyAfterTheDataConnectionCloses()
    {
        // The 226 arrives only once the data connection is closed. Skipping it
        // leaves the control connection one reply out of step, and the next
        // command reads this one's answer.
        var channel = new ScriptedChannel();
        var backend = new FtpBlobBackend(channel);

        await backend.GetAsync(BlobPath.Create(TenantA, "q3.bin"), default);

        Assert.Equal(1, channel.CompletionRepliesRead);
    }

    [Fact]
    public async Task PutAsync_CreatesEveryAncestorDirectory()
    {
        var channel = new ScriptedChannel();
        var backend = new FtpBlobBackend(channel);

        await backend.PutAsync(BlobPath.Create(TenantA, "docs", "q3.bin"), [1], default);

        List<string> made = channel.Commands
            .Where(c => c.StartsWith("MKD ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [
                "MKD tenants",
                "MKD tenants/" + TenantA.ToString("D"),
                "MKD tenants/" + TenantA.ToString("D") + "/docs",
            ],
            made);
    }

    // ── the store contract ────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_Of550_IsNotFound()
    {
        var channel = new ScriptedChannel { TransferCode = 550 };
        var backend = new FtpBlobBackend(channel);

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "missing.bin"), default);

        Assert.True(read.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, read.Error!.Code);
    }

    [Fact]
    public async Task GetAsync_OfAServerError_IsATransientProviderFailure()
    {
        // 421 is "service not available". A storage outage must not look like
        // data loss.
        var channel = new ScriptedChannel { TransferCode = 421 };
        var backend = new FtpBlobBackend(channel);

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "q3.bin"), default);

        Assert.Equal(ErrorCodes.ProviderFailure, read.Error!.Code);
        Assert.Equal(ErrorCategory.Transient, read.Error.Category);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheTransferredBytes()
    {
        var channel = new ScriptedChannel { DownloadPayload = [9, 9, 9] };
        var backend = new FtpBlobBackend(channel);

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "q3.bin"), default);

        Assert.Equal([9, 9, 9], read.Value);
    }

    [Fact]
    public async Task ListAsync_ExcludesSidecarsAndTrimsLineEndings()
    {
        var channel = new ScriptedChannel
        {
            DownloadPayload = Encoding.UTF8.GetBytes("one.bin\r\none.bin.edpfmeta\r\ntwo.bin\r\n"),
        };

        var backend = new FtpBlobBackend(channel);

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/", default);

        Assert.Equal(["one.bin", "two.bin"], listed.Value);
    }

    [Fact]
    public async Task ListAsync_OfAMissingDirectory_IsEmptyRatherThanAFailure()
    {
        var channel = new ScriptedChannel { TransferCode = 550 };
        var backend = new FtpBlobBackend(channel);

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/nothing/", default);

        Assert.True(listed.IsSuccess);
        Assert.Empty(listed.Value);
    }

    [Fact]
    public async Task MetadataRoundTripsThroughASidecarFile()
    {
        var channel = new ScriptedChannel();
        var backend = new FtpBlobBackend(channel);
        BlobPath path = BlobPath.Create(TenantA, "q3.bin");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["edpf.classification"] = "Phi",
            ["edpf.declared-content-type"] = "text/html; charset=utf-8",
        };

        await backend.PutMetadataAsync(path, metadata, default);

        Assert.Contains(channel.Commands, c => c.EndsWith(".edpfmeta", StringComparison.Ordinal));

        channel.DownloadPayload = channel.LastUpload;
        Result<IReadOnlyDictionary<string, string>> read = await backend.GetMetadataAsync(path, default);

        Assert.True(read.IsSuccess);
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            Assert.Equal(entry.Value, read.Value[entry.Key]);
        }
    }

    [Fact]
    public async Task RemoveAsync_OfAnAbsentFile_IsNotFound()
    {
        var channel = new ScriptedChannel { DeleteCode = 550 };
        var backend = new FtpBlobBackend(channel);

        Result removed = await backend.RemoveAsync(BlobPath.Create(TenantA, "missing.bin"), default);

        Assert.True(removed.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, removed.Error!.Code);
    }

    [Fact]
    public void Backend_RefusesAPlainChannelWhenFtpsIsRequired()
    {
        // Refused at composition, not per operation. A deployment that
        // intended FTPS and got plain FTP should fail to start rather than
        // find out after the first record has crossed the wire in the clear.
        var plain = new ScriptedChannel { IsEncrypted = false };

        Assert.Throws<ArgumentException>(() => new FtpBlobBackend(plain));
    }

    [Fact]
    public void Backend_AcceptsAPlainChannelOnlyWhenSaidSoExplicitly()
    {
        var plain = new ScriptedChannel { IsEncrypted = false };

        var backend = new FtpBlobBackend(plain, requireEncryptedChannel: false);

        Assert.False(backend.RequireEncryptedChannel);
    }

    /// <summary>A channel that answers from a script and records what it was asked.</summary>
    private sealed class ScriptedChannel : IFtpChannel
    {
        public bool IsEncrypted { get; set; } = true;

        public List<string> Commands { get; } = [];

        public int CompletionRepliesRead { get; private set; }

        public byte[] DownloadPayload { get; set; } = [];

        public byte[] LastUpload { get; private set; } = [];

        public bool EpsvSupported { get; set; } = true;

        public int TransferCode { get; set; } = 150;

        public int DeleteCode { get; set; } = 250;

        public Task<FtpResponse> CommandAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);

            if (command == "EPSV")
            {
                return Task.FromResult(EpsvSupported
                    ? new FtpResponse(229, "229 Entering Extended Passive Mode (|||50000|)")
                    : new FtpResponse(500, "500 Unknown command"));
            }

            if (command == "PASV")
            {
                return Task.FromResult(new FtpResponse(227, "227 Entering Passive Mode (10,0,0,1,195,80)"));
            }

            if (command.StartsWith("DELE", StringComparison.Ordinal))
            {
                return Task.FromResult(new FtpResponse(DeleteCode, "delete"));
            }

            if (command.StartsWith("RETR", StringComparison.Ordinal)
                || command.StartsWith("STOR", StringComparison.Ordinal)
                || command.StartsWith("NLST", StringComparison.Ordinal))
            {
                return Task.FromResult(new FtpResponse(TransferCode, "transfer"));
            }

            return Task.FromResult(new FtpResponse(200, "200 OK"));
        }

        public Task<Stream> OpenDataAsync(FtpDataEndpoint endpoint, CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new CapturingStream(this, DownloadPayload));

        public Task<FtpResponse> ReadReplyAsync(CancellationToken cancellationToken)
        {
            CompletionRepliesRead++;
            return Task.FromResult(new FtpResponse(226, "226 Transfer complete"));
        }

        public void Dispose()
        {
        }

        internal void Captured(byte[] bytes) => LastUpload = bytes;

        /// <summary>Serves the scripted download and captures whatever is written.</summary>
        private sealed class CapturingStream(ScriptedChannel owner, byte[] download) : MemoryStream
        {
            private readonly MemoryStream _written = new();

            public override bool CanRead => true;

            public override int Read(byte[] buffer, int offset, int count)
            {
                int remaining = download.Length - (int)_position;
                int taken = Math.Min(count, remaining);

                Array.Copy(download, _position, buffer, offset, taken);
                _position += taken;

                return taken;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _written.Write(buffer, offset, count);
                owner.Captured(_written.ToArray());
            }

            protected override void Dispose(bool disposing)
            {
                owner.Captured(_written.ToArray());
                base.Dispose(disposing);
            }

            private long _position;
        }
    }
}
