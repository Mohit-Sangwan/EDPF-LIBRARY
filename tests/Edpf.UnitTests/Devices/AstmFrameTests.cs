using Edpf.Abstractions.Primitives;
using Edpf.Devices;

namespace Edpf.UnitTests.Devices;

/// <summary>
/// Phase 24f — ASTM E1381 framing, the protocol laboratory instruments speak
/// over RS-232.
/// </summary>
public sealed class AstmFrameTests
{
    [Fact]
    public void Checksum_IsTheLowByteOfTheSum_HandVerifiable()
    {
        // Body "1A" + ETX: '1' = 0x31, 'A' = 0x41, ETX = 0x03.
        // 0x31 + 0x41 + 0x03 = 0x75. A reader can check this without running
        // it, which is the point of choosing a short example.
        Assert.Equal("75", AstmFrame.Checksum("1A" + AstmFrame.Etx));
    }

    [Fact]
    public void Checksum_WrapsAtOneByte()
    {
        // The sum is taken modulo 256, so a long frame does not overflow into
        // a three-digit checksum the receiver cannot parse.
        string body = new string('ÿ', 4);

        Assert.Equal(2, AstmFrame.Checksum(body).Length);
    }

    [Fact]
    public void Frame_RoundTrips()
    {
        string frame = AstmFrame.Build(1, "R|1|^^^Glucose|5.4|mmol/L", isFinal: true).Value;

        AstmFrameContent parsed = AstmFrame.Parse(frame).Value;

        Assert.Equal(1, parsed.FrameNumber);
        Assert.Equal("R|1|^^^Glucose|5.4|mmol/L", parsed.Text);
        Assert.True(parsed.IsFinal);
    }

    [Fact]
    public void IntermediateFrame_UsesEtb_AndIsNotFinal()
    {
        string frame = AstmFrame.Build(2, "partial", isFinal: false).Value;

        Assert.Contains(AstmFrame.Etb, frame);
        Assert.False(AstmFrame.Parse(frame).Value.IsFinal);
    }

    [Fact]
    public void SingleFlippedBit_IsDetected()
    {
        // The whole reason the checksum exists. A serial cable in a laboratory
        // runs past centrifuges and compressors; a flipped bit in a result
        // value is a wrong result delivered with full confidence.
        string frame = AstmFrame.Build(1, "R|1|^^^Glucose|5.4|mmol/L", isFinal: true).Value;

        // Corrupt one character of the payload, leaving the checksum intact.
        char[] corrupted = frame.ToCharArray();
        int index = frame.IndexOf("5.4", StringComparison.Ordinal);
        corrupted[index] = '9';

        Result<AstmFrameContent> parsed = AstmFrame.Parse(new string(corrupted));

        Assert.True(parsed.IsFailure);
        Assert.Contains("corrupted in transit", parsed.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlCharacterInThePayload_IsRefused()
    {
        // It would terminate the frame early and the remainder would be read
        // as a different frame. Refused rather than escaped — E1381 has no
        // escape sequence.
        Result<string> result = AstmFrame.Build(1, "before" + AstmFrame.Etx + "after", isFinal: true);

        Assert.True(result.IsFailure);
        Assert.Contains("framing control character", result.Error!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void FrameNumberOutsideZeroToSeven_IsRefused(int frameNumber)
    {
        Assert.True(AstmFrame.Build(frameNumber, "x", isFinal: true).IsFailure);
    }

    [Fact]
    public void FrameNumber_CyclesZeroToSeven()
    {
        // Sequence checking is what makes a dropped or duplicated frame
        // visible. A retransmission that silently replaces a different result
        // is worse than a gap, because a gap gets noticed.
        Assert.Equal(1, AstmFrame.NextFrameNumber(0));
        Assert.Equal(0, AstmFrame.NextFrameNumber(7));
    }

    [Fact]
    public void EmptyPayload_IsValid()
    {
        string frame = AstmFrame.Build(0, string.Empty, isFinal: true).Value;

        Assert.Equal(string.Empty, AstmFrame.Parse(frame).Value.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a frame")]
    [InlineData("1A")]
    public void MalformedFrame_IsRefusedRatherThanThrown(string data)
    {
        // A device on a cable sends whatever it sends, including nothing and
        // garbage. Every malformation is an expected input.
        Result<AstmFrameContent> parsed = AstmFrame.Parse(data);

        Assert.True(parsed.IsFailure);
        Assert.Equal(ErrorCategory.Validation, parsed.Error!.Category);
    }

    [Fact]
    public void FrameWithoutTerminator_IsRefused()
    {
        string malformed = AstmFrame.Stx + "1payload" + "AB" + AstmFrame.Cr + AstmFrame.Lf;

        Assert.True(AstmFrame.Parse(malformed).IsFailure);
    }

    [Fact]
    public void FrameNotEndingInCrLf_IsRefused()
    {
        string frame = AstmFrame.Build(1, "x", isFinal: true).Value;
        string truncated = frame.Substring(0, frame.Length - 2);

        Assert.True(AstmFrame.Parse(truncated).IsFailure);
    }
}
