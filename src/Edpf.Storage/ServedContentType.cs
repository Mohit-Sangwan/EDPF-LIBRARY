using System;
using System.Collections.Generic;

namespace Edpf.Storage;

/// <summary>
/// Decides what media type the platform is willing to serve for a stored blob,
/// which is not necessarily the one the uploader declared.
/// </summary>
/// <remarks>
/// <para>
/// The attack this closes is stored cross-site scripting. A user uploads a
/// file, declares it <c>text/html</c>, and the platform later serves it back
/// from its own origin with that header. The browser executes it with the
/// victim's session — and the "file store" has become a script host for
/// anything that can upload.
/// </para>
/// <para>
/// **The list is an allow-list, and that is the entire design.** A deny-list of
/// dangerous types is a list of the ones somebody thought of:
/// <c>image/svg+xml</c> looks like an image and executes script; XHTML and XML
/// with a stylesheet both do; and the set grows with every browser release. An
/// allow-list is wrong in the safe direction — a new harmless type downloads
/// instead of rendering until somebody adds it, which is an inconvenience, not
/// an incident.
/// </para>
/// </remarks>
public static class ServedContentType
{
    /// <summary>What anything not provably inline-safe is served as.</summary>
    public const string Fallback = "application/octet-stream";

    /// <summary>
    /// The media types that may be rendered inline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raster images and plain text. Nothing else, and in particular:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <c>image/svg+xml</c> is absent. SVG is a document format that
    ///     executes script, and its presence in a list of "images" is the
    ///     single most common way this control is defeated.
    ///   </item>
    ///   <item>
    ///     <c>application/pdf</c> is absent. A PDF is a program — it carries
    ///     JavaScript, forms and external references — and ADR-033 already
    ///     settled how this framework treats artefacts that are programs.
    ///     Clinical reports download; they do not render in-origin.
    ///   </item>
    /// </list>
    /// </remarks>
    private static readonly HashSet<string> InlineSafeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "image/bmp",
        "image/tiff",
        "text/plain",
    };

    /// <summary>
    /// Resolves the served media type and whether the blob must download.
    /// </summary>
    /// <param name="declaredContentType">The media type the uploader claimed.</param>
    /// <returns>
    /// The type to put in a <c>Content-Type</c> header, and whether a
    /// <c>Content-Disposition: attachment</c> must accompany it.
    /// </returns>
    /// <remarks>
    /// Parameters on the declared type (<c>; charset=…</c>, <c>; boundary=…</c>)
    /// are discarded rather than echoed. They are caller-controlled bytes that
    /// would otherwise reach a response header, and a header is the last place
    /// to start trusting input.
    /// </remarks>
    public static ContentTypeDecision Resolve(string? declaredContentType)
    {
        if (string.IsNullOrWhiteSpace(declaredContentType))
        {
            return new ContentTypeDecision(Fallback, requiresAttachment: true);
        }

        string candidate = declaredContentType!;

        int parameterStart = candidate.IndexOf(';', StringComparison.Ordinal);
        if (parameterStart >= 0)
        {
            candidate = candidate.Substring(0, parameterStart);
        }

        candidate = candidate.Trim();

        if (!IsWellFormedMediaType(candidate))
        {
            return new ContentTypeDecision(Fallback, requiresAttachment: true);
        }

        // Matched case-insensitively, but what comes back is the *stored*
        // spelling, not the caller's. `IMAGE/PNG` is the same media type and is
        // served as `image/png` — a response header should carry the
        // platform's canonical form, not an echo of whatever arrived.
        return InlineSafeTypes.TryGetValue(candidate, out string? canonical)
            ? new ContentTypeDecision(canonical, requiresAttachment: false)
            : new ContentTypeDecision(Fallback, requiresAttachment: true);
    }

    /// <summary>
    /// True when the string is <c>type/subtype</c> over the RFC 9110 token
    /// character set, with exactly one solidus and no empty part.
    /// </summary>
    /// <remarks>
    /// This runs before the allow-list lookup, not after, so a value carrying a
    /// carriage return or a newline is rejected as malformed rather than
    /// compared. Header injection is not a thing the allow-list would catch:
    /// <c>image/png\r\nSet-Cookie: …</c> is not in the set, so it would fall
    /// back safely — but relying on that is relying on an accident.
    /// </remarks>
    private static bool IsWellFormedMediaType(string value)
    {
        int solidus = -1;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (c == '/')
            {
                if (solidus >= 0)
                {
                    return false;
                }

                solidus = i;
                continue;
            }

            bool isToken = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c is '+' or '-' or '.' or '_';

            if (!isToken)
            {
                return false;
            }
        }

        return solidus > 0 && solidus < value.Length - 1;
    }
}

/// <summary>
/// The outcome of <see cref="ServedContentType.Resolve"/>: what to serve, and
/// whether it must download.
/// </summary>
public readonly struct ContentTypeDecision : IEquatable<ContentTypeDecision>
{
    /// <summary>
    /// Records a decision.
    /// </summary>
    /// <param name="servedContentType">The media type to serve.</param>
    /// <param name="requiresAttachment">Whether the blob must download rather than render.</param>
    public ContentTypeDecision(string servedContentType, bool requiresAttachment)
    {
        ServedContentType = servedContentType;
        RequiresAttachment = requiresAttachment;
    }

    /// <summary>The media type the platform will serve.</summary>
    public string ServedContentType { get; }

    /// <summary>Whether a <c>Content-Disposition: attachment</c> is required.</summary>
    public bool RequiresAttachment { get; }

    /// <inheritdoc />
    public bool Equals(ContentTypeDecision other)
        => RequiresAttachment == other.RequiresAttachment
            && string.Equals(ServedContentType, other.ServedContentType, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContentTypeDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => unchecked((StringComparer.Ordinal.GetHashCode(ServedContentType) * 397) ^ (RequiresAttachment ? 1 : 0));

    /// <summary>Value equality.</summary>
    public static bool operator ==(ContentTypeDecision left, ContentTypeDecision right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ContentTypeDecision left, ContentTypeDecision right) => !left.Equals(right);
}
