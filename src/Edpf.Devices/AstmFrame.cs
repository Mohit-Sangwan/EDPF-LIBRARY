using System;
using System.Globalization;
using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Devices;

/// <summary>
/// ASTM E1381 low-level framing, as spoken by laboratory instruments over
/// RS-232 (Phase 24f).
/// </summary>
/// <remarks>
/// <para>
/// A frame is
/// <c>&lt;STX&gt; FN text &lt;ETB|ETX&gt; C1 C2 &lt;CR&gt;&lt;LF&gt;</c>, where
/// <c>FN</c> is a single frame number cycling 0-7 and <c>C1 C2</c> are the
/// checksum as two uppercase hex digits.
/// </para>
/// <para>
/// **The checksum and the frame number are not ceremony.** A serial cable in a
/// laboratory runs past centrifuges and refrigeration compressors; line noise
/// flips bits, and a flipped bit in a result value is a wrong result delivered
/// with full confidence. The checksum is what makes that corruption visible,
/// and the cycling frame number is what makes a dropped or duplicated frame
/// visible — a retransmission that silently replaces a different result is
/// worse than a gap.
/// </para>
/// <para>
/// Tier 3 (ADR-002): plain <see cref="string"/> and
/// <see cref="StringBuilder"/> operations throughout, no <c>Index</c>,
/// <c>Range</c> or <c>System.Buffers</c> — this assembly has to build on
/// net472, because that is where the hardware is.
/// </para>
/// </remarks>
public static class AstmFrame
{
    /// <summary>Start of text.</summary>
    public const char Stx = '';

    /// <summary>End of text — the last frame of a message.</summary>
    public const char Etx = '';

    /// <summary>End of transmission block — more frames follow.</summary>
    public const char Etb = '';

    /// <summary>Carriage return.</summary>
    public const char Cr = '\r';

    /// <summary>Line feed.</summary>
    public const char Lf = '\n';

    /// <summary>
    /// Builds a frame.
    /// </summary>
    /// <param name="frameNumber">The frame number, 0 to 7.</param>
    /// <param name="text">The frame payload.</param>
    /// <param name="isFinal">Whether this is the message's last frame.</param>
    /// <returns>The framed text, or a failure.</returns>
    public static Result<string> Build(int frameNumber, string text, bool isFinal)
    {
        Guard.NotNull(text, nameof(text));

        if (frameNumber < 0 || frameNumber > 7)
        {
            return Result.Failure<string>(new Error(
                ErrorCodes.ValidationFailed,
                "An ASTM frame number cycles 0 to 7.",
                ErrorCategory.Validation));
        }

        foreach (char c in text)
        {
            // A control character in the payload would terminate the frame
            // early, and the remainder would be read as the next frame's
            // header. Refused rather than escaped — E1381 has no escape.
            if (c == Stx || c == Etx || c == Etb || c == Cr || c == Lf)
            {
                return Result.Failure<string>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The frame payload contains a framing control character, which would terminate the "
                    + "frame early and cause the remainder to be read as a different frame.",
                    ErrorCategory.Validation));
            }
        }

        var body = new StringBuilder();
        body.Append((char)('0' + frameNumber));
        body.Append(text);
        body.Append(isFinal ? Etx : Etb);

        string checksum = Checksum(body.ToString());

        var frame = new StringBuilder();
        frame.Append(Stx).Append(body).Append(checksum).Append(Cr).Append(Lf);

        return Result.Success(frame.ToString());
    }

    /// <summary>
    /// Parses a frame, verifying its checksum.
    /// </summary>
    /// <param name="frame">The received frame.</param>
    /// <returns>The frame number, payload and finality, or a failure.</returns>
    public static Result<AstmFrameContent> Parse(string frame)
    {
        Guard.NotNull(frame, nameof(frame));

        // STX + FN + terminator + 2 checksum + CR + LF is the shortest legal
        // frame, and it carries an empty payload.
        if (frame.Length < 7 || frame[0] != Stx)
        {
            return Failure("The data is not an ASTM frame.");
        }

        if (frame[frame.Length - 2] != Cr || frame[frame.Length - 1] != Lf)
        {
            return Failure("The frame does not end with CR LF.");
        }

        char frameNumberChar = frame[1];
        if (frameNumberChar < '0' || frameNumberChar > '7')
        {
            return Failure("The frame number is not in the range 0 to 7.");
        }

        int terminatorIndex = -1;
        for (int i = 2; i < frame.Length - 4; i++)
        {
            if (frame[i] == Etx || frame[i] == Etb)
            {
                terminatorIndex = i;
                break;
            }
        }

        if (terminatorIndex < 0)
        {
            return Failure("The frame carries no ETX or ETB terminator.");
        }

        // Substring rather than a Range expression: this assembly builds on
        // net472, where System.Range does not exist (ADR-002).
        string body = frame.Substring(1, terminatorIndex);
        string received = frame.Substring(terminatorIndex + 1, 2);
        string expected = Checksum(body);

        if (!string.Equals(received, expected, StringComparison.OrdinalIgnoreCase))
        {
            // The whole reason the checksum exists. A flipped bit in a result
            // value is a wrong result delivered with full confidence.
            return Failure(
                $"The frame checksum is {received} but the content computes to {expected}; the frame was "
                + "corrupted in transit.");
        }

        return Result.Success(new AstmFrameContent(
            frameNumberChar - '0',
            frame.Substring(2, terminatorIndex - 2),
            frame[terminatorIndex] == Etx));
    }

    /// <summary>
    /// The ASTM E1381 checksum: the low byte of the sum of the frame body,
    /// as two uppercase hex digits.
    /// </summary>
    /// <param name="body">The frame number, payload and terminator.</param>
    /// <returns>Two uppercase hex digits.</returns>
    public static string Checksum(string body)
    {
        Guard.NotNull(body, nameof(body));

        int sum = 0;
        foreach (char c in body)
        {
            sum += c;
        }

        // InvariantCulture: a checksum that formatted differently under a
        // different server locale would fail against the same instrument in
        // another region (Phase 27).
        return (sum & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The frame number that must follow <paramref name="previous"/>.
    /// </summary>
    /// <param name="previous">The previous frame number.</param>
    /// <returns>The expected next number, cycling 0 to 7.</returns>
    /// <remarks>
    /// Sequence checking is what makes a dropped or duplicated frame visible.
    /// A retransmission that silently replaces a different result is worse
    /// than a gap, because a gap gets noticed.
    /// </remarks>
    public static int NextFrameNumber(int previous) => (previous + 1) % 8;

    private static Result<AstmFrameContent> Failure(string message)
        => Result.Failure<AstmFrameContent>(new Error(
            ErrorCodes.ValidationFailed, message, ErrorCategory.Validation));
}

/// <summary>The contents of a parsed ASTM frame (Phase 24f).</summary>
public sealed class AstmFrameContent
{
    /// <summary>Initializes frame contents.</summary>
    /// <param name="frameNumber">The frame number, 0 to 7.</param>
    /// <param name="text">The payload.</param>
    /// <param name="isFinal">Whether this is the message's last frame.</param>
    public AstmFrameContent(int frameNumber, string text, bool isFinal)
    {
        FrameNumber = frameNumber;
        Text = Guard.NotNull(text, nameof(text));
        IsFinal = isFinal;
    }

    /// <summary>The frame number, 0 to 7.</summary>
    public int FrameNumber { get; }

    /// <summary>The payload.</summary>
    public string Text { get; }

    /// <summary>Whether this is the message's last frame.</summary>
    public bool IsFinal { get; }
}
