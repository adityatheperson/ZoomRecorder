namespace ZoomRecorder.App.Data;

public sealed class ArtifactStore
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
        ValidateArtifactName(artifactName);
        if (content.IsEmpty)
        {
            throw new ArgumentException("Artifact content cannot be empty.", nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ownerDirectory = Path.Combine(_artifactsRoot, ownerId.ToString("D"));
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
