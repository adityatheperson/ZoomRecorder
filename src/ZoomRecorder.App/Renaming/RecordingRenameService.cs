using System.Globalization;
using Microsoft.Data.Sqlite;
using ZoomRecorder.App.Data;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Renaming;

internal interface IRecordingRenameFileSystem
{
    Task MoveAsync(string source, string destination, CancellationToken cancellationToken);
    void Move(string source, string destination);
}

public enum RecordingRenameErrorCode
{
    InvalidName,
    NameInUse,
    ProcessingActive,
    FileUnavailable
}

public sealed class RecordingRenameException(RecordingRenameErrorCode code, string message)
    : InvalidOperationException(message)
{
    public RecordingRenameErrorCode Code { get; } = code;
}

public sealed class RecordingRenameService
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly LibraryDatabase database;
    private readonly LibraryPaths paths;
    private readonly IRecordingRenameFileSystem fileSystem;

    public RecordingRenameService(LibraryDatabase database, LibraryPaths paths)
        : this(database, paths, new PhysicalRecordingRenameFileSystem())
    {
    }

    internal RecordingRenameService(
        LibraryDatabase database,
        LibraryPaths paths,
        IRecordingRenameFileSystem fileSystem)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public async Task<RecordingRecord> RenameAsync(
        Guid recordingId,
        string requestedName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = NormalizeFileName(requestedName);
        await database.Gate.WaitAsync(cancellationToken);
        try
        {
            var recording = await LoadRecordingAsync(recordingId, cancellationToken);
            await EnsureNoActiveJobAsync(recordingId, cancellationToken);
            var original = RecordingRenamePathSafety.ValidateFileWithinRoot(paths.RecordingsRoot, recording.FilePath);
            if (!File.Exists(original) ||
                !string.Equals(Path.GetFileName(original), recording.FileName, StringComparison.Ordinal))
            {
                throw new RecordingRenameException(
                    RecordingRenameErrorCode.FileUnavailable,
                    "The recording file is unavailable.");
            }

            var destination = RecordingRenamePathSafety.ValidateFileWithinRoot(
                paths.RecordingsRoot,
                Path.Combine(Path.GetDirectoryName(original)!, fileName));
            if (string.Equals(original, destination, StringComparison.OrdinalIgnoreCase))
            {
                return recording;
            }
            if (File.Exists(destination))
            {
                throw new RecordingRenameException(
                    RecordingRenameErrorCode.NameInUse,
                    "A recording with that name already exists.");
            }

            await WriteJournalAsync(recordingId, original, destination, fileName, cancellationToken);
            try
            {
                await fileSystem.MoveAsync(original, destination, cancellationToken);
            }
            catch
            {
                if (File.Exists(original) && !File.Exists(destination))
                {
                    await RemoveJournalAsync(recordingId, CancellationToken.None);
                }
                throw;
            }
            try
            {
                await UpdateDatabaseAsync(recordingId, destination, fileName, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RestoreOriginalFileOrThrow(original, destination);
                await RemoveJournalAsync(recordingId, CancellationToken.None);
                throw;
            }
            catch (Exception failure)
            {
                try
                {
                    RestoreOriginalFileOrThrow(original, destination);
                    await RemoveJournalAsync(recordingId, CancellationToken.None);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(failure, rollbackFailure);
                }

                throw new RecordingRenameException(
                    RecordingRenameErrorCode.FileUnavailable,
                    "The recording could not be renamed.");
            }
            return recording with { FilePath = destination, FileName = fileName };
        }
        finally
        {
            database.Gate.Release();
        }
    }

    private async Task UpdateDatabaseAsync(
        Guid recordingId,
        string destination,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await database.Connection.BeginTransactionAsync(cancellationToken);
        await using (var command = database.Connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE recordings SET file_path = $path, file_name = $name WHERE id = $id;";
            command.Parameters.AddWithValue("$path", destination);
            command.Parameters.AddWithValue("$name", fileName);
            command.Parameters.AddWithValue("$id", recordingId.ToString("D", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var jobs = database.Connection.CreateCommand())
        {
            jobs.Transaction = transaction;
            jobs.CommandText = "UPDATE processing_jobs SET mp4_path = $path WHERE recording_id = $id;";
            jobs.Parameters.AddWithValue("$path", destination);
            jobs.Parameters.AddWithValue("$id", recordingId.ToString("D", CultureInfo.InvariantCulture));
            await jobs.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var journal = database.Connection.CreateCommand())
        {
            journal.Transaction = transaction;
            journal.CommandText = "DELETE FROM recording_rename_journal WHERE recording_id = $id;";
            journal.Parameters.AddWithValue("$id", recordingId.ToString("D", CultureInfo.InvariantCulture));
            await journal.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private void RestoreOriginalFileOrThrow(string original, string destination)
    {
        var originalExists = File.Exists(original);
        var destinationExists = File.Exists(destination);
        if (!originalExists && destinationExists)
        {
            fileSystem.Move(destination, original);
        }

        if (!File.Exists(original) || File.Exists(destination))
        {
            throw new IOException(
                "The interrupted recording rename is unresolved; recovery information was preserved.");
        }
    }

    private async Task WriteJournalAsync(
        Guid recordingId,
        string original,
        string destination,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_rename_journal(
                recording_id, original_path, renamed_path, renamed_file_name)
            VALUES ($id, $original, $renamed, $fileName);
            """;
        command.Parameters.AddWithValue("$id", recordingId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$original", original);
        command.Parameters.AddWithValue("$renamed", destination);
        command.Parameters.AddWithValue("$fileName", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RemoveJournalAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "DELETE FROM recording_rename_journal WHERE recording_id = $id;";
        command.Parameters.AddWithValue("$id", recordingId.ToString("D", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureNoActiveJobAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT state FROM processing_jobs WHERE recording_id = $id;";
        command.Parameters.AddWithValue("$id", recordingId.ToString("D", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0) is not ("Completed" or "Cancelled" or "NeedsAttention"))
            {
                throw new RecordingRenameException(
                    RecordingRenameErrorCode.ProcessingActive,
                    "Wait for processing to finish before renaming this recording.");
            }
        }
    }

    private static string NormalizeFileName(string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            throw InvalidName();
        }

        var stem = requestedName;
        if (stem.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        var fileName = stem + ".mp4";
        if (stem.Length == 0 || stem is "." or ".." ||
            stem.EndsWith('.') || stem.EndsWith(' ') ||
            fileName.Length > 255 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            ReservedWindowsNames.Contains(stem.Split('.')[0]))
        {
            throw InvalidName();
        }

        return fileName;
    }

    private static RecordingRenameException InvalidName() => new(
        RecordingRenameErrorCode.InvalidName,
        "Enter a valid Windows file name.");

    private async Task<RecordingRecord> LoadRecordingAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = """
            SELECT id, class_id, file_path, file_name, meeting_id, recorded_at,
                   duration_ms, byte_size, video_available
            FROM recordings WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", recordingId.ToString("D", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("The recording does not exist.");
        }

        return new RecordingRecord(
            Guid.ParseExact(reader.GetString(0), "D"),
            reader.IsDBNull(1) ? null : Guid.ParseExact(reader.GetString(1), "D"),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            DateTimeOffset.ParseExact(reader.GetString(5), "O", CultureInfo.InvariantCulture),
            TimeSpan.FromMilliseconds(reader.GetInt64(6)),
            reader.GetInt64(7),
            reader.GetInt64(8) != 0);
    }
}

internal sealed class PhysicalRecordingRenameFileSystem : IRecordingRenameFileSystem
{
    public Task MoveAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(source, destination);
        return Task.CompletedTask;
    }

    public void Move(string source, string destination) => File.Move(source, destination);
}
