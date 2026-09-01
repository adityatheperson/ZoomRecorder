using System.Globalization;
using ZoomRecorder.App.Data;

namespace ZoomRecorder.App.Renaming;

internal sealed class RecordingRenameRecoveryService
{
    private readonly LibraryDatabase database;
    private readonly LibraryPaths paths;

    internal RecordingRenameRecoveryService(LibraryDatabase database, LibraryPaths paths)
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
                cancellationToken.ThrowIfCancellationRequested();
                var original = RecordingRenamePathSafety.ValidateFileWithinRoot(paths.RecordingsRoot, entry.OriginalPath);
                var renamed = RecordingRenamePathSafety.ValidateFileWithinRoot(paths.RecordingsRoot, entry.RenamedPath);
                if (!string.Equals(Path.GetDirectoryName(original), Path.GetDirectoryName(renamed), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(renamed), entry.RenamedFileName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The recording rename journal is invalid.");
                }

                var storedPath = await ReadStoredPathAsync(entry.RecordingId, cancellationToken);
                Reconcile(storedPath, original, renamed);
                await RemoveEntryAsync(entry.RecordingId, cancellationToken);
            }
        }
        finally
        {
            database.Gate.Release();
        }
    }

    private async Task<RenameJournalEntry[]> LoadEntriesAsync(CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = """
            SELECT recording_id, original_path, renamed_path, renamed_file_name
            FROM recording_rename_journal;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<RenameJournalEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out var recordingId))
            {
                throw new InvalidDataException("The recording rename journal is invalid.");
            }

            entries.Add(new RenameJournalEntry(
                recordingId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return entries.ToArray();
    }

    private async Task<string> ReadStoredPathAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM recordings WHERE id = $id;";
        command.Parameters.AddWithValue("$id", recordingId.ToString("D", CultureInfo.InvariantCulture));
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException("The recording rename journal has no recording.");
    }

    private static void Reconcile(string storedPath, string original, string renamed)
    {
        var databaseUsesOriginal = string.Equals(storedPath, original, StringComparison.OrdinalIgnoreCase);
        var databaseUsesRenamed = string.Equals(storedPath, renamed, StringComparison.OrdinalIgnoreCase);
        if (!databaseUsesOriginal && !databaseUsesRenamed)
        {
            throw new InvalidDataException("The recording rename journal does not match the library.");
        }

        var originalExists = File.Exists(original);
        var renamedExists = File.Exists(renamed);
        if (originalExists == renamedExists)
        {
            throw new InvalidDataException("The interrupted recording rename is ambiguous.");
        }

        if (databaseUsesOriginal && renamedExists)
        {
            File.Move(renamed, original);
        }
        else if (databaseUsesRenamed && originalExists)
        {
            File.Move(original, renamed);
        }
    }

    private async Task RemoveEntryAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "DELETE FROM recording_rename_journal WHERE recording_id = $id;";
        command.Parameters.AddWithValue("$id", recordingId.ToString("D", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record RenameJournalEntry(
        Guid RecordingId,
        string OriginalPath,
        string RenamedPath,
        string RenamedFileName);
}

internal static class RecordingRenamePathSafety
{
    internal static string ValidateFileWithinRoot(string rootPath, string filePath)
    {
        if (!Path.IsPathFullyQualified(rootPath) || !Path.IsPathFullyQualified(filePath))
        {
            throw new InvalidDataException("A recording rename path is not fully qualified.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var file = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(root, file);
        if (relative == "." || Path.IsPathFullyQualified(relative) ||
            relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A recording rename path is outside the recordings folder.");
        }

        EnsureNoReparsePoints(root, file);

        return file;
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        if ((File.Exists(root) || Directory.Exists(root)) &&
            File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The recordings folder is a reparse point.");
        }

        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("A recording rename path crosses a reparse point.");
            }
        }
    }
}
