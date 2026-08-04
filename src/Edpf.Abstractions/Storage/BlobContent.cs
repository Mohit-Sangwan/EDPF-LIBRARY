using System;
using System.IO;

namespace Edpf.Abstractions.Storage;

/// <summary>
/// A blob's plaintext together with the metadata describing it.
/// </summary>
/// <remarks>
/// The two travel together on purpose. A stream handed back on its own loses
/// the classification and the served content type, and the caller then has to
/// remember to fetch them separately — which is exactly the step that gets
/// skipped on the path that renders the file.
/// </remarks>
public sealed class BlobContent : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Pairs content with its descriptor.
    /// </summary>
    /// <param name="descriptor">The blob's metadata.</param>
    /// <param name="content">The plaintext stream, positioned at the start.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public BlobContent(BlobDescriptor descriptor, Stream content)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    /// <summary>The blob's metadata.</summary>
    public BlobDescriptor Descriptor { get; }

    /// <summary>The plaintext stream. The caller owns it and must dispose this instance.</summary>
    public Stream Content { get; }

    /// <summary>Disposes the underlying stream.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Content.Dispose();
        _disposed = true;
    }
}
