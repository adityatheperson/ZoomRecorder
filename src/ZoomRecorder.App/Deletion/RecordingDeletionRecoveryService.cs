using ZoomRecorder.App.Data;

namespace ZoomRecorder.App.Deletion;

internal enum RecordingDeletionTargetKind
{
    Video,
    RecordingArtifacts,
    JobDirectory,
    ClassGuide
}

internal sealed record RecordingDeletionJournalEntry(
    Guid RecordingId,
    Guid OwnerId,
    RecordingDeletionTargetKind Kind,
    string OriginalPath,
    string QuarantinePath,
    bool IsDirectory);

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
}

internal static class RecordingDeletionFileSafety
{
    internal static void DeleteDirectoryTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    DeleteDirectoryTree(entry);
                }
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
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
            var entries = await LoadEntriesAsync(cancellationToken);
            foreach (var entry in entries)
            {
                Validate(entry);
            }

            var existence = new Dictionary<Guid, bool>();
            var restoreFailures = new List<Exception>();
            foreach (var entry in entries.OrderByDescending(item => item.OriginalPath.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!existence.TryGetValue(entry.RecordingId, out var recordingExists))
                {
                    recordingExists = await RecordingExistsAsync(entry.RecordingId, cancellationToken);
                    existence.Add(entry.RecordingId, recordingExists);
                }

                if (recordingExists)
                {
                    try
                    {
                        Restore(entry);
                    }
                    catch (Exception failure)
                    {
                        restoreFailures.Add(failure);
                    }
                }
                else
                {
                    TryPurge(entry);
                }
            }

            await RemoveResolvedEntriesAsync(entries, cancellationToken);
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

    private async Task<RecordingDeletionJournalEntry[]> LoadEntriesAsync(CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = """
            SELECT recording_id, owner_id, target_kind, original_path, quarantine_path, is_directory
            FROM recording_deletion_journal;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<RecordingDeletionJournalEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out var recordingId) ||
                !Guid.TryParseExact(reader.GetString(1), "D", out var ownerId) ||
                !Enum.TryParse<RecordingDeletionTargetKind>(reader.GetString(2), out var kind) ||
                !Enum.IsDefined(kind))
            {
                throw new InvalidDataException("The recording deletion journal is invalid.");
            }

            entries.Add(new RecordingDeletionJournalEntry(
                recordingId,
                ownerId,
                kind,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) switch
                {
                    0 => false,
                    1 => true,
                    _ => throw new InvalidDataException("The recording deletion journal is invalid.")
                }));
        }

        return entries.ToArray();
    }

    private void Validate(RecordingDeletionJournalEntry entry)
    {
        if (!Path.IsPathFullyQualified(entry.OriginalPath) ||
            !Path.IsPathFullyQualified(entry.QuarantinePath))
        {
            throw new InvalidDataException("A recording deletion journal path is not fully qualified.");
        }

        var original = Path.GetFullPath(entry.OriginalPath);
        var expectedQuarantine = RecordingDeletionQuarantine.PathFor(original, entry.RecordingId);
        if (!string.Equals(
                Path.GetFullPath(entry.QuarantinePath), expectedQuarantine, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A recording deletion journal path is inconsistent.");
        }

        switch (entry.Kind)
        {
            case RecordingDeletionTargetKind.Video
                when !entry.IsDirectory && entry.OwnerId == entry.RecordingId:
                ValidateWithinRoot(paths.RecordingsRoot, original, requireDirectChild: false);
                break;
            case RecordingDeletionTargetKind.RecordingArtifacts
                when entry.IsDirectory && entry.OwnerId == entry.RecordingId:
                ValidateExactChild(paths.ArtifactsRoot, original, entry.RecordingId);
                break;
            case RecordingDeletionTargetKind.JobDirectory when entry.IsDirectory:
                ValidateExactChild(paths.JobsRoot, original, entry.OwnerId);
                break;
            case RecordingDeletionTargetKind.ClassGuide when !entry.IsDirectory:
                var classDirectory = Path.Combine(paths.ArtifactsRoot, entry.OwnerId.ToString("D"));
                ValidateWithinRoot(classDirectory, original, requireDirectChild: false);
                ValidateWithinRoot(paths.ArtifactsRoot, classDirectory, requireDirectChild: true);
                break;
            default:
                throw new InvalidDataException("A recording deletion journal owner is inconsistent.");
        }

        EnsureNoReparsePoints(RootFor(entry), original);
        EnsureNoReparsePoints(RootFor(entry), expectedQuarantine);
    }

    private string RootFor(RecordingDeletionJournalEntry entry) => entry.Kind switch
    {
        RecordingDeletionTargetKind.Video => paths.RecordingsRoot,
        RecordingDeletionTargetKind.JobDirectory => paths.JobsRoot,
        _ => paths.ArtifactsRoot
    };

    private static void ValidateExactChild(string root, string path, Guid ownerId)
    {
        var expected = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(root, ownerId.ToString("D"))));
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A recording deletion journal owner path is inconsistent.");
        }
    }

    private static void ValidateWithinRoot(string root, string path, bool requireDirectChild)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalPath = Path.GetFullPath(path);
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!canonicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            (requireDirectChild && !string.Equals(
                Path.GetDirectoryName(canonicalPath), canonicalRoot, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("A recording deletion journal path is outside its trusted root.");
        }
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
        var current = canonicalRoot;
        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("A recording deletion path crosses a reparse point.");
            }
        }
    }

    private async Task<bool> RecordingExistsAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM recordings WHERE id = $recordingId);";
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static void Restore(RecordingDeletionJournalEntry entry)
    {
        if (!File.Exists(entry.QuarantinePath) && !Directory.Exists(entry.QuarantinePath))
        {
            return;
        }

        if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
        {
            throw new IOException("An interrupted deletion cannot be restored over an existing path.");
        }

        if (entry.IsDirectory)
        {
            Directory.Move(entry.QuarantinePath, entry.OriginalPath);
        }
        else
        {
            File.Move(entry.QuarantinePath, entry.OriginalPath);
        }
    }

    private static void TryPurge(RecordingDeletionJournalEntry entry)
    {
        try
        {
            if (entry.IsDirectory && Directory.Exists(entry.QuarantinePath))
            {
                RecordingDeletionFileSafety.DeleteDirectoryTree(entry.QuarantinePath);
            }
            else if (!entry.IsDirectory && File.Exists(entry.QuarantinePath))
            {
                File.Delete(entry.QuarantinePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task RemoveResolvedEntriesAsync(
        IReadOnlyList<RecordingDeletionJournalEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            if (File.Exists(entry.QuarantinePath) || Directory.Exists(entry.QuarantinePath))
            {
                continue;
            }

            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                DELETE FROM recording_deletion_journal
                WHERE recording_id = $recordingId AND original_path = $originalPath;
                """;
            command.Parameters.AddWithValue("$recordingId", entry.RecordingId.ToString("D"));
            command.Parameters.AddWithValue("$originalPath", entry.OriginalPath);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
