using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Core.Guards;

namespace Edpf.Storage;

/// <summary>
/// Stores blobs as files under a root directory.
/// </summary>
/// <remarks>
/// <para>
/// Content and metadata live in two sibling trees — <c>blobs/</c> and
/// <c>meta/</c> — rather than as neighbouring files. A sidecar named
/// <c>report.pdf.meta</c> is a filename a caller can also upload, and the
/// resulting collision would let one blob overwrite another's classification.
/// Separate trees make that unrepresentable instead of unlikely.
/// </para>
/// <para>
/// The full-path containment check below is redundant: every path arrives as a
/// <see cref="BlobPath"/>, which rejects traversal at construction. It is kept
/// because it is three lines, it costs nothing, and it is the difference
/// between "traversal is impossible" and "traversal is impossible provided the
/// only other check is correct".
/// </para>
/// </remarks>
public sealed class FileSystemBlobBackend : IBlobBackend
{
    private const string ContentFolder = "blobs";
    private const string MetadataFolder = "meta";
    private const string MetadataExtension = ".json";

    private readonly string _root;

    /// <summary>
    /// Roots the backend at a directory, creating it if absent.
    /// </summary>
    /// <param name="rootDirectory">The directory that contains everything this backend stores.</param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is blank.</exception>
    public FileSystemBlobBackend(string rootDirectory)
    {
        Guard.NotNullOrWhiteSpace(rootDirectory, nameof(rootDirectory));

        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(Path.Combine(_root, ContentFolder));
        Directory.CreateDirectory(Path.Combine(_root, MetadataFolder));
    }

    /// <inheritdoc />
    public string BackendName => "FileSystem";

    /// <inheritdoc />
    public async Task<Result> PutAsync(BlobPath path, byte[] bytes, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(bytes, nameof(bytes));

        Result<string> resolved = Resolve(ContentFolder, path.Value);
        if (resolved.IsFailure)
        {
            return Result.Failure(resolved.Error!);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.Value)!);
            await File.WriteAllBytesAsync(resolved.Value, bytes, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (IOException)
        {
            return Result.Failure(ProviderFailure());
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure(ProviderFailure());
        }
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> GetAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<string> resolved = Resolve(ContentFolder, path.Value);
        if (resolved.IsFailure)
        {
            return Result.Failure<byte[]>(resolved.Error!);
        }

        try
        {
            return await File.ReadAllBytesAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return Result.Failure<byte[]>(NotFound());
        }
        catch (DirectoryNotFoundException)
        {
            return Result.Failure<byte[]>(NotFound());
        }
        catch (IOException)
        {
            return Result.Failure<byte[]>(ProviderFailure());
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure<byte[]>(ProviderFailure());
        }
    }

    /// <inheritdoc />
    public Task<Result> RemoveAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();

        Result<string> content = Resolve(ContentFolder, path.Value);
        if (content.IsFailure)
        {
            return Task.FromResult(Result.Failure(content.Error!));
        }

        Result<string> metadata = Resolve(MetadataFolder, path.Value + MetadataExtension);
        if (metadata.IsFailure)
        {
            return Task.FromResult(Result.Failure(metadata.Error!));
        }

        try
        {
            if (!File.Exists(content.Value))
            {
                return Task.FromResult(Result.Failure(NotFound()));
            }

            File.Delete(content.Value);

            if (File.Exists(metadata.Value))
            {
                File.Delete(metadata.Value);
            }

            return Task.FromResult(Result.Success());
        }
        catch (IOException)
        {
            return Task.FromResult(Result.Failure(ProviderFailure()));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Failure(ProviderFailure()));
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<string>>> ListAsync(string renderedPrefix, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(renderedPrefix, nameof(renderedPrefix));
        cancellationToken.ThrowIfCancellationRequested();

        string normalized = renderedPrefix.TrimEnd('/');
        Result<string> resolved = Resolve(ContentFolder, normalized);
        if (resolved.IsFailure)
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<string>>(resolved.Error!));
        }

        if (!Directory.Exists(resolved.Value))
        {
            return Task.FromResult(Result<IReadOnlyList<string>>.FromValue(Array.Empty<string>()));
        }

        string contentRoot = Path.Combine(_root, ContentFolder);
        var rendered = new List<string>();

        foreach (string file in Directory.EnumerateFiles(resolved.Value, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(contentRoot, file);
            rendered.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
        }

        return Task.FromResult(Result<IReadOnlyList<string>>.FromValue(rendered));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        BlobPath path,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        Result<string> resolved = Resolve(MetadataFolder, path.Value + MetadataExtension);
        if (resolved.IsFailure)
        {
            return Result.Failure<IReadOnlyDictionary<string, string>>(resolved.Error!);
        }

        try
        {
            string json = await File.ReadAllTextAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
            Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            return parsed is null
                ? Result.Failure<IReadOnlyDictionary<string, string>>(NotFound())
                : Result<IReadOnlyDictionary<string, string>>.FromValue(parsed);
        }
        catch (FileNotFoundException)
        {
            return Result.Failure<IReadOnlyDictionary<string, string>>(NotFound());
        }
        catch (DirectoryNotFoundException)
        {
            return Result.Failure<IReadOnlyDictionary<string, string>>(NotFound());
        }
        catch (JsonException)
        {
            return Result.Failure<IReadOnlyDictionary<string, string>>(NotFound());
        }
        catch (IOException)
        {
            return Result.Failure<IReadOnlyDictionary<string, string>>(ProviderFailure());
        }
    }

    /// <inheritdoc />
    public async Task<Result> PutMetadataAsync(
        BlobPath path,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(metadata, nameof(metadata));

        Result<string> resolved = Resolve(MetadataFolder, path.Value + MetadataExtension);
        if (resolved.IsFailure)
        {
            return Result.Failure(resolved.Error!);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.Value)!);

            // Serialized as JSON rather than a line-oriented format because one
            // of these values is the caller's declared content type. A newline
            // in it would silently rewrite the sidecar under any format that
            // treats newlines as structure.
            string json = JsonSerializer.Serialize(metadata);
            await File.WriteAllTextAsync(resolved.Value, json, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (IOException)
        {
            return Result.Failure(ProviderFailure());
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure(ProviderFailure());
        }
    }

    private Result<string> Resolve(string folder, string renderedPath)
    {
        string baseDirectory = Path.Combine(_root, folder);
        string combined = Path.GetFullPath(Path.Combine(baseDirectory, renderedPath));

        string prefix = baseDirectory.EndsWith(Path.DirectorySeparatorChar.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? baseDirectory
            : baseDirectory + Path.DirectorySeparatorChar;

        return combined.StartsWith(prefix, StringComparison.Ordinal)
            ? combined
            : Result.Failure<string>(NotFound());
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
