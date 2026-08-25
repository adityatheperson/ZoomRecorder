using System.Security.Cryptography;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Data;

public sealed class ArtifactStore : IProcessingArtifactStore
{
    private readonly string _artifactsRoot;
    private readonly Func<CancellationToken, ValueTask> _beforeCommit;

    public ArtifactStore(string artifactsRoot)
        : this(artifactsRoot, _ => ValueTask.CompletedTask)
    {
    }

    internal ArtifactStore(
        string artifactsRoot,
        Func<CancellationToken, ValueTask> beforeCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsRoot);
        _artifactsRoot = Path.GetFullPath(artifactsRoot);
        _beforeCommit = beforeCommit ?? throw new ArgumentNullException(nameof(beforeCommit));
    }

    public async Task<string> WriteAtomicallyAsync(
        Guid ownerId,
        string artifactName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var ownerDirectory = Path.Combine(_artifactsRoot, ownerId.ToString("D"));
        return await WriteToDirectoryAtomicallyAsync(
            ownerDirectory, artifactName, content, cancellationToken);
    }

    public async Task<bool> VerifyAsync(
        string path,
        string sha256,
        long? expectedByteSize,
        CancellationToken cancellationToken)
    {
        ValidatePath(path, nameof(path));
        ValidateHash(sha256, nameof(sha256));
        if (expectedByteSize is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteSize));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = Path.GetFullPath(path);
        if (!File.Exists(canonicalPath))
        {
            return false;
        }

        try
        {
            if (expectedByteSize is { } size && new FileInfo(canonicalPath).Length != size)
            {
                return false;
            }

            await using var stream = new FileStream(
                canonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            return string.Equals(actual, sha256, StringComparison.Ordinal);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public async Task<ArtifactCheckpoint> WriteJobArtifactAsync(
        ProcessingRequest request,
        string artifactName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePath(request.JobDirectory, nameof(request));
        var path = await WriteToDirectoryAtomicallyAsync(
            Path.GetFullPath(request.JobDirectory), artifactName, content, cancellationToken);
        return new ArtifactCheckpoint(path, Hash(content.Span));
    }

    public async Task<ArtifactCheckpoint> WriteRecordingArtifactAsync(
        Guid recordingId,
        string artifactName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var path = await WriteAtomicallyAsync(recordingId, artifactName, content, cancellationToken);
        return new ArtifactCheckpoint(path, Hash(content.Span));
    }

    public async Task<ArtifactCheckpoint> WriteClassArtifactAsync(
        Guid classId,
        string artifactName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var path = await WriteAtomicallyAsync(classId, artifactName, content, cancellationToken);
        return new ArtifactCheckpoint(path, Hash(content.Span));
    }

    public async Task<ReadOnlyMemory<byte>?> ReadVerifiedAsync(
        ArtifactCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidatePath(checkpoint.Path, nameof(checkpoint));
        ValidateHash(checkpoint.Sha256, nameof(checkpoint));
        try
        {
            var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(checkpoint.Path), cancellationToken);
            return string.Equals(Hash(bytes), checkpoint.Sha256, StringComparison.Ordinal)
                ? new ReadOnlyMemory<byte>(bytes)
                : (ReadOnlyMemory<byte>?)null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public Task CleanupJobAsync(
        ProcessingRequest request,
        IReadOnlyCollection<string> publishedPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishedPaths);
        ValidatePath(request.JobDirectory, nameof(request));
        var jobDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.JobDirectory));
        if (Path.GetDirectoryName(jobDirectory) is null)
        {
            throw new ArgumentException("The job directory cannot be a file-system root.", nameof(request));
        }

        var published = publishedPaths.Select(path =>
        {
            ValidatePath(path, nameof(publishedPaths));
            return Path.GetFullPath(path);
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(jobDirectory))
        {
            return Task.CompletedTask;
        }

        foreach (var path in Directory.EnumerateFiles(jobDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonicalPath = Path.GetFullPath(path);
            if (!published.Contains(canonicalPath) && IsUnpublishedSpoolFile(canonicalPath))
            {
                File.Delete(canonicalPath);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<string> WriteToDirectoryAtomicallyAsync(
        string directory,
        string artifactName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ValidateArtifactName(artifactName);
        if (content.IsEmpty)
        {
            throw new ArgumentException("Artifact content cannot be empty.", nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ownerDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(ownerDirectory);
        var destination = Path.GetFullPath(Path.Combine(ownerDirectory, artifactName));
        var temporaryPath = Path.Combine(ownerDirectory, $".{artifactName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65_536,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
                if (stream.Length == 0)
                {
                    throw new IOException("The artifact temporary file is empty.");
                }
            }

            await _beforeCommit(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsUnpublishedSpoolFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("local-audio-", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("local-whisper-", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".local-whisper-", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("transcript-chunk-", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static void ValidatePath(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, name);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be fully qualified.", name);
        }
    }

    private static void ValidateHash(string hash, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash, name);
        if (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 value is required.", name);
        }
    }

    private static void ValidateArtifactName(string artifactName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);
        if (Path.IsPathRooted(artifactName) ||
            !string.Equals(Path.GetFileName(artifactName), artifactName, StringComparison.Ordinal) ||
            artifactName is "." or ".." ||
            artifactName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Artifact name must be a valid leaf file name.", nameof(artifactName));
        }
    }
}
