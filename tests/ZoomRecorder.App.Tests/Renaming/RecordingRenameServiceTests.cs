using ZoomRecorder.App.Data;
using ZoomRecorder.App.Renaming;
using ZoomRecorder.Core.Library;
using Microsoft.Data.Sqlite;

namespace ZoomRecorder.App.Tests.Renaming;

public sealed class RecordingRenameServiceTests
{
    [Fact]
    public async Task Rename_moves_the_mp4_and_updates_the_library_record()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000001");
        var originalPath = temp.CreateRecording("lecture.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);

        var renamed = await new RecordingRenameService(database, paths)
            .RenameAsync(recordingId, "Biology Week 1", default);

        var expectedPath = Path.Combine(paths.RecordingsRoot, "Biology Week 1.mp4");
        Assert.False(File.Exists(originalPath));
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(expectedPath, renamed.FilePath);
        Assert.Equal("Biology Week 1.mp4", renamed.FileName);
        var stored = Assert.Single(await repository.ListRecordingsAsync(null, default));
        Assert.Equal(expectedPath, stored.FilePath);
        Assert.Equal("Biology Week 1.mp4", stored.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/name")]
    [InlineData("CON")]
    [InlineData("CON.notes")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public async Task Rename_rejects_invalid_windows_file_names_without_changing_the_recording(string requestedName)
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000002");
        var originalPath = temp.CreateRecording("lecture.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);

        var error = await Assert.ThrowsAsync<RecordingRenameException>(() =>
            new RecordingRenameService(database, paths).RenameAsync(recordingId, requestedName, default));

        Assert.Equal(RecordingRenameErrorCode.InvalidName, error.Code);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("lecture.mp4", Assert.Single(await repository.ListRecordingsAsync(null, default)).FileName);
    }

    [Fact]
    public async Task Rename_rejects_an_existing_destination_without_overwriting_either_file()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000003");
        var originalPath = temp.CreateRecording("lecture.mp4");
        var existingPath = temp.CreateRecording("Existing.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);

        var error = await Assert.ThrowsAsync<RecordingRenameException>(() =>
            new RecordingRenameService(database, paths).RenameAsync(recordingId, "Existing.mp4", default));

        Assert.Equal(RecordingRenameErrorCode.NameInUse, error.Code);
        Assert.True(File.Exists(originalPath));
        Assert.True(File.Exists(existingPath));
        Assert.Equal("test", File.ReadAllText(existingPath));
    }

    [Fact]
    public async Task Rename_rejects_a_recording_with_an_active_processing_job()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var classRecord = await repository.CreateClassAsync("Biology", null, default);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000004");
        var originalPath = temp.CreateRecording("lecture.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, classRecord.Id, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);
        await SeedJobAsync(database.Connection, recordingId, classRecord.Id, originalPath, "Transcribing");

        var error = await Assert.ThrowsAsync<RecordingRenameException>(() =>
            new RecordingRenameService(database, paths).RenameAsync(recordingId, "Renamed", default));

        Assert.Equal(RecordingRenameErrorCode.ProcessingActive, error.Code);
        Assert.True(File.Exists(originalPath));
        Assert.Equal(originalPath, await ReadJobMp4PathAsync(database.Connection, recordingId));
    }

    [Fact]
    public async Task Rename_updates_the_mp4_path_for_completed_processing_jobs()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var classRecord = await repository.CreateClassAsync("Biology", null, default);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000005");
        var originalPath = temp.CreateRecording("lecture.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, classRecord.Id, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);
        await SeedJobAsync(database.Connection, recordingId, classRecord.Id, originalPath, "Completed");

        await new RecordingRenameService(database, paths).RenameAsync(recordingId, "Renamed", default);

        Assert.Equal(
            Path.Combine(paths.RecordingsRoot, "Renamed.mp4"),
            await ReadJobMp4PathAsync(database.Connection, recordingId));
    }

    [Fact]
    public async Task Rename_restores_the_original_file_when_the_database_update_fails()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000006");
        var originalPath = temp.CreateRecording("lecture.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);
        await using (var trigger = database.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER reject_recording_rename
                BEFORE UPDATE OF file_path ON recordings
                BEGIN
                    SELECT RAISE(ABORT, 'simulated database failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<RecordingRenameException>(() =>
            new RecordingRenameService(database, paths).RenameAsync(recordingId, "Renamed", default));

        Assert.Equal(RecordingRenameErrorCode.FileUnavailable, error.Code);
        Assert.True(File.Exists(originalPath));
        Assert.False(File.Exists(Path.Combine(paths.RecordingsRoot, "Renamed.mp4")));
        var stored = Assert.Single(await repository.ListRecordingsAsync(null, default));
        Assert.Equal(originalPath, stored.FilePath);
        Assert.Equal("lecture.mp4", stored.FileName);
    }

    [Fact]
    public async Task Recovery_restores_a_file_moved_before_its_database_update()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000007");
        var originalPath = temp.CreateRecording("lecture.mp4");
        var renamedPath = Path.Combine(paths.RecordingsRoot, "Renamed.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);
        await SeedRenameJournalAsync(database.Connection, recordingId, originalPath, renamedPath, "Renamed.mp4");
        File.Move(originalPath, renamedPath);

        await new RecordingRenameRecoveryService(database, paths).RecoverAsync(default);

        Assert.True(File.Exists(originalPath));
        Assert.False(File.Exists(renamedPath));
        Assert.Equal(originalPath, Assert.Single(await repository.ListRecordingsAsync(null, default)).FilePath);
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "recording_rename_journal"));
    }

    [Fact]
    public async Task Rename_journals_the_move_before_touching_the_file_and_clears_it_after_commit()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000008");
        var originalPath = temp.CreateRecording("lecture.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);
        long journalRowsAtMove = -1;
        var fileSystem = new CallbackRenameFileSystem(async () =>
        {
            journalRowsAtMove = await CountRowsAsync(database.Connection, "recording_rename_journal");
        });

        await new RecordingRenameService(database, paths, fileSystem)
            .RenameAsync(recordingId, "Renamed", default);

        Assert.Equal(1L, journalRowsAtMove);
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "recording_rename_journal"));
    }

    [Theory]
    [InlineData("lecture")]
    [InlineData("Lecture")]
    public async Task Rename_to_the_current_name_ignoring_case_is_a_no_op(string requestedName)
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000009");
        var originalPath = temp.CreateRecording("lecture.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);

        var unchanged = await new RecordingRenameService(database, paths)
            .RenameAsync(recordingId, requestedName, default);

        Assert.Equal(originalPath, unchanged.FilePath);
        Assert.True(File.Exists(originalPath));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "recording_rename_journal"));
    }

    [Fact]
    public async Task Database_failure_preserves_the_journal_when_rollback_is_ambiguous()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("91000000-0000-0000-0000-000000000010");
        var originalPath = temp.CreateRecording("lecture.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, originalPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"), TimeSpan.FromMinutes(45), 4, true), default);
        await using (var trigger = database.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER reject_ambiguous_rename
                BEFORE UPDATE OF file_path ON recordings
                BEGIN
                    SELECT RAISE(ABORT, 'simulated database failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<AggregateException>(() =>
            new RecordingRenameService(database, paths, new AmbiguousRollbackFileSystem())
                .RenameAsync(recordingId, "Renamed", default));

        Assert.True(File.Exists(originalPath));
        Assert.True(File.Exists(Path.Combine(paths.RecordingsRoot, "Renamed.mp4")));
        Assert.Equal(1L, await CountRowsAsync(database.Connection, "recording_rename_journal"));
    }

    private static async Task SeedJobAsync(
        SqliteConnection connection,
        Guid recordingId,
        Guid classId,
        string mp4Path,
        string state)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_jobs(
                id, recording_id, class_id, mp4_path, job_directory, state, failed_stage,
                delete_video, error_code, transcript_committed, lecture_package_committed,
                assignments_committed, guide_outcome, revision, created_at, updated_at)
            VALUES (
                $id, $recordingId, $classId, $mp4Path, $jobDirectory, $state, NULL,
                0, NULL, 0, 0, 0, 'NotAttempted', 0, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
        command.Parameters.AddWithValue("$classId", classId.ToString("D"));
        command.Parameters.AddWithValue("$mp4Path", mp4Path);
        command.Parameters.AddWithValue("$jobDirectory", Path.Combine(Path.GetDirectoryName(mp4Path)!, Guid.NewGuid().ToString("D")));
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$now", "2026-08-31T12:00:00.0000000+00:00");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadJobMp4PathAsync(SqliteConnection connection, Guid recordingId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT mp4_path FROM processing_jobs WHERE recording_id = $recordingId;";
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SeedRenameJournalAsync(
        SqliteConnection connection,
        Guid recordingId,
        string originalPath,
        string renamedPath,
        string renamedFileName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_rename_journal(
                recording_id, original_path, renamed_path, renamed_file_name)
            VALUES ($recordingId, $originalPath, $renamedPath, $renamedFileName);
            """;
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
        command.Parameters.AddWithValue("$originalPath", originalPath);
        command.Parameters.AddWithValue("$renamedPath", renamedPath);
        command.Parameters.AddWithValue("$renamedFileName", renamedFileName);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ZoomRecorder.Rename.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            LibraryPaths = new LibraryPaths(
                System.IO.Path.Combine(Path, "library.db"),
                System.IO.Path.Combine(Path, "artifacts"),
                System.IO.Path.Combine(Path, "jobs"),
                System.IO.Path.Combine(Path, "recordings"));
        }

        public string Path { get; }
        public LibraryPaths LibraryPaths { get; }

        public string CreateRecording(string fileName)
        {
            Directory.CreateDirectory(LibraryPaths.RecordingsRoot);
            var path = System.IO.Path.Combine(LibraryPaths.RecordingsRoot, fileName);
            File.WriteAllText(path, "test");
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class CallbackRenameFileSystem(Func<Task> beforeMove) : IRecordingRenameFileSystem
    {
        public async Task MoveAsync(string source, string destination, CancellationToken cancellationToken)
        {
            await beforeMove();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(source, destination);
        }

        public void Move(string source, string destination) => File.Move(source, destination);
    }

    private sealed class AmbiguousRollbackFileSystem : IRecordingRenameFileSystem
    {
        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(source, destination);
            File.WriteAllText(source, "interloper");
            return Task.CompletedTask;
        }

        public void Move(string source, string destination) => File.Move(source, destination);
    }
}
