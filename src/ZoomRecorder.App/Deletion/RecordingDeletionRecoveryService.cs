using ZoomRecorder.App.Data;

namespace ZoomRecorder.App.Deletion;

internal static class RecordingDeletionQuarantine
{
    internal const string Marker = ".zoomrecorder-delete-";

    internal static string PathFor(string originalPath, Guid recordingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        if (recordingId == Guid.Empty)
        {
            throw new ArgumentException("A recording identifier is required.", nameof(recordingId));
        }

        return Path.GetFullPath(originalPath) + Marker + recordingId.ToString("D");
    }

    internal static bool TryParse(string quarantinePath, out string originalPath, out Guid recordingId)
    {
        recordingId = default;
        var markerIndex = quarantinePath.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0 ||
            !Guid.TryParseExact(quarantinePath[(markerIndex + Marker.Length)..], "D", out recordingId))
        {
            originalPath = string.Empty;
            return false;
        }

        originalPath = quarantinePath[..markerIndex];
        return true;
    }
}

internal sealed class RecordingDeletionRecoveryService
{
    private readonly LibraryDatabase database;
    private readonly LibraryPaths paths;

    internal RecordingDeletionRecoveryService(LibraryDatabase database, LibraryPaths paths)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await database.Gate.WaitAsync(cancellationToken);
        try
        {
            var existence = new Dictionary<Guid, bool>();
            var restoreFailures = new List<Exception>();
            foreach (var quarantinePath in EnumerateQuarantines())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!RecordingDeletionQuarantine.TryParse(
                        quarantinePath, out var originalPath, out var recordingId))
                {
                    continue;
                }

                if (!existence.TryGetValue(recordingId, out var recordingExists))
                {
                    recordingExists = await RecordingExistsAsync(recordingId, cancellationToken);
                    existence.Add(recordingId, recordingExists);
                }

                if (recordingExists)
                {
                    try
                    {
                        Restore(quarantinePath, originalPath);
                    }
                    catch (Exception failure)
                    {
                        restoreFailures.Add(failure);
                    }
                }
                else
                {
                    TryPurge(quarantinePath);
                }
            }

            if (restoreFailures.Count > 0)
            {
                throw new AggregateException(
                    "One or more interrupted recording deletions could not be recovered.",
                    restoreFailures);
            }
        }
        finally
        {
            database.Gate.Release();
        }
    }

    private string[] EnumerateQuarantines()
    {
        var roots = new[] { paths.RecordingsRoot, paths.JobsRoot, paths.ArtifactsRoot }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         root,
                         $"*{RecordingDeletionQuarantine.Marker}*",
                         SearchOption.AllDirectories))
            {
                result.Add(Path.GetFullPath(entry));
            }
        }

        return result.ToArray();
    }

    private async Task<bool> RecordingExistsAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM recordings WHERE id = $recordingId);";
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static void Restore(string quarantinePath, string originalPath)
    {
        if (File.Exists(originalPath) || Directory.Exists(originalPath))
        {
            throw new IOException("An interrupted deletion cannot be restored over an existing path.");
        }

        if (Directory.Exists(quarantinePath))
        {
            Directory.Move(quarantinePath, originalPath);
        }
        else if (File.Exists(quarantinePath))
        {
            File.Move(quarantinePath, originalPath);
        }
    }

    private static void TryPurge(string quarantinePath)
    {
        try
        {
            if (Directory.Exists(quarantinePath))
            {
                Directory.Delete(quarantinePath, recursive: true);
            }
            else if (File.Exists(quarantinePath))
            {
                File.Delete(quarantinePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
