using Microsoft.Data.Sqlite;
using ZoomRecorder.App.Data;
using ZoomRecorder.App.Deletion;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Tests.Deletion;

public sealed class RecordingDeletionServiceTests
{
    [Fact]
    public async Task Delete_removes_video_processing_files_study_artifacts_and_database_rows()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var classRecord = await repository.CreateClassAsync("Biology", "Fall 2026", default);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000001");
        var jobId = Guid.Parse("82000000-0000-0000-0000-000000000001");
        var videoPath = temp.CreateFile("recordings", "lecture.mp4");
        var recording = await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, classRecord.Id, videoPath, "lecture.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var recordingArtifact = temp.CreateFile("artifacts", recordingId.ToString("D"), "summary.json");
        var jobDirectory = temp.CreateDirectory("jobs", jobId.ToString("D"));
        var audioArtifact = temp.CreateFile("jobs", jobId.ToString("D"), "audio.m4a");
        var transcriptArtifact = temp.CreateFile("jobs", jobId.ToString("D"), "transcript.json");
        var classGuideArtifact = temp.CreateFile("artifacts", classRecord.Id.ToString("D"), "guide.json");
        await SeedProcessingDataAsync(
            database.Connection, recording, classRecord.Id, jobId, jobDirectory,
            audioArtifact, transcriptArtifact, recordingArtifact, classGuideArtifact, "Completed");

        await new RecordingDeletionService(database, paths).DeleteAsync(recordingId, default);

        Assert.False(File.Exists(videoPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(recordingArtifact)));
        Assert.False(Directory.Exists(jobDirectory));
        Assert.False(File.Exists(classGuideArtifact));
        Assert.Empty(await repository.ListRecordingsAsync(null, default));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "processing_jobs"));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "audio_chunks"));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "transcription_chunks"));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "processing_transcripts"));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "lecture_packages"));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "assignments"));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "class_study_guides"));
    }

    [Fact]
    public async Task Delete_rejects_an_active_processing_job_without_removing_files_or_metadata()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var classRecord = await repository.CreateClassAsync("Chemistry", null, default);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000002");
        var jobId = Guid.Parse("82000000-0000-0000-0000-000000000002");
        var videoPath = temp.CreateFile("recordings", "active.mp4");
        var recording = await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, classRecord.Id, videoPath, "active.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var recordingArtifact = temp.CreateFile("artifacts", recordingId.ToString("D"), "summary.json");
        var jobDirectory = temp.CreateDirectory("jobs", jobId.ToString("D"));
        var audioArtifact = temp.CreateFile("jobs", jobId.ToString("D"), "audio.m4a");
        var transcriptArtifact = temp.CreateFile("jobs", jobId.ToString("D"), "transcript.json");
        var classGuideArtifact = temp.CreateFile("artifacts", classRecord.Id.ToString("D"), "guide.json");
        await SeedProcessingDataAsync(
            database.Connection, recording, classRecord.Id, jobId, jobDirectory,
            audioArtifact, transcriptArtifact, recordingArtifact, classGuideArtifact, "Transcribing");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RecordingDeletionService(database, paths).DeleteAsync(recordingId, default));

        Assert.True(File.Exists(videoPath));
        Assert.True(Directory.Exists(jobDirectory));
        Assert.Single(await repository.ListRecordingsAsync(null, default));
        Assert.Equal(1L, await CountRowsAsync(database.Connection, "processing_jobs"));
    }

    [Fact]
    public async Task Delete_rejects_a_locked_file_and_preserves_the_library_entry()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000003");
        var videoPath = temp.CreateFile("recordings", "locked.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, videoPath, "locked.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        await using var locked = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() =>
            new RecordingDeletionService(database, paths).DeleteAsync(recordingId, default));

        Assert.True(File.Exists(videoPath));
        Assert.Single(await repository.ListRecordingsAsync(null, default));
    }

    [Fact]
    public async Task Delete_treats_already_missing_files_as_successful_cleanup()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000004");
        var missingVideo = System.IO.Path.Combine(temp.Path, "recordings", "missing.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, missingVideo, "missing.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);

        await new RecordingDeletionService(database, paths).DeleteAsync(recordingId, default);

        Assert.Empty(await repository.ListRecordingsAsync(null, default));
    }

    private static async Task SeedProcessingDataAsync(
        SqliteConnection connection,
        RecordingRecord recording,
        Guid classId,
        Guid jobId,
        string jobDirectory,
        string audioArtifact,
        string transcriptArtifact,
        string recordingArtifact,
        string classGuideArtifact,
        string state)
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_jobs(
                id, recording_id, class_id, mp4_path, job_directory, state, failed_stage,
                delete_video, error_code, transcript_committed, lecture_package_committed,
                assignments_committed, guide_outcome, revision, created_at, updated_at)
            VALUES ($jobId, $recordingId, $classId, $mp4, $jobDirectory, $state, NULL,
                0, NULL, 1, 1, 1, 'Succeeded', 1, $now, $now);
            INSERT INTO audio_chunks(job_id, chunk_index, start_ms, end_ms, sha256, artifact_path, byte_size)
            VALUES ($jobId, 0, 0, 1000, $hash, $audio, 10);
            INSERT INTO transcription_chunks(job_id, chunk_index, audio_sha256, artifact_path, artifact_sha256)
            VALUES ($jobId, 0, $hash, $transcript, $hash);
            INSERT INTO processing_transcripts(job_id, artifact_path, artifact_sha256)
            VALUES ($jobId, $transcript, $hash);
            INSERT INTO lecture_packages(
                recording_id, schema_version, artifact_path, artifact_sha256,
                source_transcript_hash, is_stale, updated_at)
            VALUES ($recordingId, 1, $recordingArtifact, $hash, $hash, 0, $now);
            INSERT INTO assignments(
                id, recording_id, description, due_date_text, due_at, confidence,
                is_user_confirmed, source_timestamp_ms, source_order)
            VALUES ($assignmentId, $recordingId, 'Read chapter 1', 'Friday', NULL, .9, 1, 100, 0);
            INSERT INTO class_study_guides(
                class_id, schema_version, artifact_path, artifact_sha256, is_update_pending, updated_at)
            VALUES ($classId, 1, $classGuide, $hash, 0, $now);
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$recordingId", recording.Id.ToString("D"));
        command.Parameters.AddWithValue("$classId", classId.ToString("D"));
        command.Parameters.AddWithValue("$mp4", recording.FilePath);
        command.Parameters.AddWithValue("$jobDirectory", jobDirectory);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$now", "2026-08-25T12:00:00.0000000+00:00");
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$audio", audioArtifact);
        command.Parameters.AddWithValue("$transcript", transcriptArtifact);
        command.Parameters.AddWithValue("$recordingArtifact", recordingArtifact);
        command.Parameters.AddWithValue("$assignmentId", Guid.Parse("83000000-0000-0000-0000-000000000001").ToString("D"));
        command.Parameters.AddWithValue("$classGuide", classGuideArtifact);
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
                System.IO.Path.GetTempPath(), "ZoomRecorder.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            LibraryPaths = new LibraryPaths(
                System.IO.Path.Combine(Path, "library.db"),
                System.IO.Path.Combine(Path, "artifacts"),
                System.IO.Path.Combine(Path, "jobs"));
        }

        public string Path { get; }
        public LibraryPaths LibraryPaths { get; }

        public string CreateDirectory(params string[] parts)
        {
            var path = parts.Aggregate(Path, System.IO.Path.Combine);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(params string[] parts)
        {
            var path = parts.Aggregate(Path, System.IO.Path.Combine);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test");
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
