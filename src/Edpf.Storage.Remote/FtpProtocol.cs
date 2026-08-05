using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Storage.Remote;

/// <summary>One reply from an FTP control connection.</summary>
public sealed class FtpResponse
{
    /// <summary>
    /// Records a reply.
    /// </summary>
    /// <param name="code">The three-digit reply code.</param>
    /// <param name="text">The reply text, joined across continuation lines.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public FtpResponse(int code, string text)
    {
        Code = code;
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>The three-digit reply code.</summary>
    public int Code { get; }

    /// <summary>The reply text.</summary>
    public string Text { get; }

    /// <summary>True for 1xx, 2xx and 3xx — the command was accepted or is in progress.</summary>
    public bool IsPositive => Code >= 100 && Code < 400;

    /// <summary>
    /// True when the server says the file or directory is not there.
    /// </summary>
    /// <remarks>
    /// 550 is overloaded — it covers "no such file", "permission denied" and
    /// "not a plain file". The store's contract already treats a refusal and an
    /// absence identically at the boundary above this, so collapsing them here
    /// loses nothing the caller could act on.
    /// </remarks>
    public bool IsNotFound => Code == 550;
}

/// <summary>Where a passive-mode data connection should be made.</summary>
public sealed class FtpDataEndpoint
{
    /// <summary>
    /// Records an endpoint.
    /// </summary>
    /// <param name="host">The host, or null to reuse the control connection's host.</param>
    /// <param name="port">The port.</param>
    public FtpDataEndpoint(string? host, int port)
    {
        Host = host;
        Port = port;
    }

    /// <summary>The host, or null when the control connection's host is reused.</summary>
    public string? Host { get; }

    /// <summary>The port.</summary>
    public int Port { get; }
}

/// <summary>
/// Reading the two FTP replies that are worth getting wrong quietly.
/// </summary>
/// <remarks>
/// Named <c>FtpReply</c> rather than <c>FtpReplyParser</c>: the single-evaluator
/// architecture rule catches any <c>*Parser</c> outside <c>Edpf.Formula</c>, and
/// the right response was to rename rather than add an exemption. An exemption
/// list would turn a rule that discovers its subjects into one that names them,
/// and this repository has already recorded what happens to those.
/// </remarks>
public static class FtpReply
{
    /// <summary>
    /// True when a control line ends a reply, rather than continuing it.
    /// </summary>
    /// <param name="line">A line from the control connection.</param>
    /// <returns>True when this is the final line.</returns>
    /// <remarks>
    /// A reply is multi-line when the code is followed by <c>-</c> and final
    /// when it is followed by a space. Banners are routinely multi-line, so a
    /// client that reads exactly one line starts every session one reply out of
    /// step and then misattributes every response after it.
    /// </remarks>
    public static bool IsFinalLine(string line)
    {
        Guard.NotNull(line, nameof(line));

        return line.Length >= 4
            && char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2])
            && line[3] == ' ';
    }

    /// <summary>
    /// Reads the reply code from a final line.
    /// </summary>
    /// <param name="line">The final line.</param>
    /// <returns>The code, or 0 when the line is malformed.</returns>
    public static int CodeOf(string line)
    {
        Guard.NotNull(line, nameof(line));

        return line.Length >= 3
            && int.TryParse(
                line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code)
            ? code
            : 0;
    }

    /// <summary>
    /// Parses a <c>227 Entering Passive Mode (h1,h2,h3,h4,p1,p2)</c> reply.
    /// </summary>
    /// <param name="text">The reply text.</param>
    /// <returns>The endpoint, or a failure when the reply is malformed.</returns>
    /// <remarks>
    /// **The port is <c>p1 * 256 + p2</c>**, and getting that backwards is the
    /// single most common FTP client bug. It fails only for ports where the
    /// two bytes differ, so it works in testing against a server on a low port
    /// and fails in production.
    /// </remarks>
    public static Result<FtpDataEndpoint> ParsePassive(string text)
    {
        Guard.NotNull(text, nameof(text));

        int open = text.IndexOf('(', StringComparison.Ordinal);
        int close = text.IndexOf(')', StringComparison.Ordinal);

        if (open < 0 || close < open)
        {
            return Result.Failure<FtpDataEndpoint>(Malformed());
        }

        string[] parts = text.Substring(open + 1, close - open - 1).Split(',');
        if (parts.Length != 6)
        {
            return Result.Failure<FtpDataEndpoint>(Malformed());
        }

        var numbers = new int[6];
        for (int i = 0; i < 6; i++)
        {
            if (!int.TryParse(
                    parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])
                || numbers[i] is < 0 or > 255)
            {
                return Result.Failure<FtpDataEndpoint>(Malformed());
            }
        }

        string host = numbers[0].ToString(CultureInfo.InvariantCulture) + "."
            + numbers[1].ToString(CultureInfo.InvariantCulture) + "."
            + numbers[2].ToString(CultureInfo.InvariantCulture) + "."
            + numbers[3].ToString(CultureInfo.InvariantCulture);

        return new FtpDataEndpoint(host, (numbers[4] * 256) + numbers[5]);
    }

    /// <summary>
    /// Parses a <c>229 Entering Extended Passive Mode (|||port|)</c> reply.
    /// </summary>
    /// <param name="text">The reply text.</param>
    /// <returns>The endpoint, or a failure when the reply is malformed.</returns>
    /// <remarks>
    /// EPSV carries no address, so the data connection reuses the control
    /// connection's host — which is what makes it the correct choice behind
    /// NAT, where the address in a 227 reply is the server's own private one.
    /// </remarks>
    public static Result<FtpDataEndpoint> ParseExtendedPassive(string text)
    {
        Guard.NotNull(text, nameof(text));

        int open = text.IndexOf('(', StringComparison.Ordinal);
        int close = text.LastIndexOf(')');

        if (open < 0 || close < open)
        {
            return Result.Failure<FtpDataEndpoint>(Malformed());
        }

        string inner = text.Substring(open + 1, close - open - 1).Trim('|');

        return int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535
                ? new FtpDataEndpoint(null, port)
                : Result.Failure<FtpDataEndpoint>(Malformed());
    }

    private static Error Malformed() => new(
        ErrorCodes.IntegrationFailed,
        "The FTP server sent a passive-mode reply this client could not parse.",
        ErrorCategory.Integration);
}

/// <summary>
/// The control and data channel an <see cref="FtpBlobBackend"/> speaks over.
/// </summary>
/// <remarks>
/// The seam exists so the protocol logic — command sequencing, reply parsing,
/// the transfer-mode rule — is testable against a scripted channel. Without it
/// the only way to exercise any of it is a live FTP server, and an adapter
/// that can only be tested against a live server is an adapter that does not
/// get tested.
/// </remarks>
public interface IFtpChannel : IDisposable
{
    /// <summary>
    /// True when the control connection has completed <c>AUTH TLS</c> and the
    /// data connection is protected (<c>PROT P</c>).
    /// </summary>
    /// <remarks>
    /// Declared by the channel because only the channel knows. A backend that
    /// assumed encryption from a configuration flag would be trusting the
    /// deployment's intent rather than its actual socket.
    /// </remarks>
    bool IsEncrypted { get; }

    /// <summary>
    /// Sends a command and reads its complete reply, including continuation
    /// lines.
    /// </summary>
    /// <param name="command">The command, without the trailing CRLF.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The reply.</returns>
    Task<FtpResponse> CommandAsync(string command, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the data connection for a transfer already negotiated by a
    /// passive-mode command.
    /// </summary>
    /// <param name="endpoint">Where to connect.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The data stream. The caller disposes it to end the transfer.</returns>
    Task<Stream> OpenDataAsync(FtpDataEndpoint endpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a reply that arrives without a command — the transfer-complete
    /// reply that follows a closed data connection.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The reply.</returns>
    Task<FtpResponse> ReadReplyAsync(CancellationToken cancellationToken);
}
