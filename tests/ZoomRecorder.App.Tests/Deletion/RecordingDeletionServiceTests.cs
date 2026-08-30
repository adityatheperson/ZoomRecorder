using Microsoft.Data.Sqlite;
using ZoomRecorder.App.Data;
using ZoomRecorder.App.Deletion;
using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Processing;

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

    [Fact]
    public async Task Delete_rejects_a_video_path_outside_the_recordings_root_without_changing_anything()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000005");
        var unrelatedFile = temp.CreateFile("outside", "unrelated.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, unrelatedFile, "unrelated.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RecordingDeletionService(database, paths).DeleteAsync(recordingId, default));

        Assert.True(File.Exists(unrelatedFile));
        Assert.Single(await repository.ListRecordingsAsync(null, default));
    }

    [Fact]
    public async Task Delete_rejects_a_job_directory_owned_by_another_job()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var classRecord = await repository.CreateClassAsync("Physics", null, default);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000006");
        var jobId = Guid.Parse("82000000-0000-0000-0000-000000000006");
        var otherJobId = Guid.Parse("82000000-0000-0000-0000-000000000099");
        var videoPath = temp.CreateFile("recordings", "ownership.mp4");
        var recording = await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, classRecord.Id, videoPath, "ownership.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var recordingArtifact = temp.CreateFile("artifacts", recordingId.ToString("D"), "summary.json");
        var wrongJobDirectory = temp.CreateDirectory("jobs", otherJobId.ToString("D"));
        var audioArtifact = temp.CreateFile("jobs", otherJobId.ToString("D"), "audio.m4a");
        var transcriptArtifact = temp.CreateFile("jobs", otherJobId.ToString("D"), "transcript.json");
        var classGuideArtifact = temp.CreateFile("artifacts", classRecord.Id.ToString("D"), "guide.json");
        await SeedProcessingDataAsync(
            database.Connection, recording, classRecord.Id, jobId, wrongJobDirectory,
            audioArtifact, transcriptArtifact, recordingArtifact, classGuideArtifact, "Completed");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RecordingDeletionService(database, paths).DeleteAsync(recordingId, default));

        Assert.True(File.Exists(videoPath));
        Assert.True(Directory.Exists(wrongJobDirectory));
        Assert.Single(await repository.ListRecordingsAsync(null, default));
    }

    [Theory]
    [InlineData("audio_chunks")]
    [InlineData("transcription_chunks")]
    [InlineData("processing_transcripts")]
    [InlineData("lecture_packages")]
    public async Task Delete_rejects_an_artifact_path_owned_by_another_recording_or_job(string table)
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var classRecord = await repository.CreateClassAsync("Ownership", null, default);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000009");
        var jobId = Guid.Parse("82000000-0000-0000-0000-000000000009");
        var videoPath = temp.CreateFile("recordings", "artifact-ownership.mp4");
        var recording = await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, classRecord.Id, videoPath, "artifact-ownership.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var recordingArtifact = temp.CreateFile("artifacts", recordingId.ToString("D"), "summary.json");
        var jobDirectory = temp.CreateDirectory("jobs", jobId.ToString("D"));
        var audioArtifact = temp.CreateFile("jobs", jobId.ToString("D"), "audio.m4a");
        var transcriptArtifact = temp.CreateFile("jobs", jobId.ToString("D"), "transcript.json");
        var classGuideArtifact = temp.CreateFile("artifacts", classRecord.Id.ToString("D"), "guide.json");
        await SeedProcessingDataAsync(
            database.Connection, recording, classRecord.Id, jobId, jobDirectory,
            audioArtifact, transcriptArtifact, recordingArtifact, classGuideArtifact, "Completed");
        var wrongOwner = Guid.Parse("84000000-0000-0000-0000-000000000009");
        var unrelatedArtifact = table == "lecture_packages"
            ? temp.CreateFile("artifacts", wrongOwner.ToString("D"), "unrelated.json")
            : temp.CreateFile("jobs", wrongOwner.ToString("D"), "unrelated.json");
        await ReplaceArtifactPathAsync(database.Connection, table, unrelatedArtifact);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RecordingDeletionService(database, paths).DeleteAsync(recordingId, default));

        Assert.True(File.Exists(videoPath));
        Assert.True(File.Exists(unrelatedArtifact));
        Assert.Single(await repository.ListRecordingsAsync(null, default));
    }

    [Fact]
    public async Task Delete_handles_a_terminal_job_migrated_from_schema_version_one()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        var classId = Guid.Parse("80000000-0000-0000-0000-000000000007");
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000007");
        var jobId = Guid.Parse("82000000-0000-0000-0000-000000000007");
        var videoPath = temp.CreateFile("recordings", "legacy.mp4");
        var jobDirectory = temp.CreateDirectory("jobs", jobId.ToString("D"));
        var audioArtifact = temp.CreateFile("jobs", jobId.ToString("D"), "legacy.m4a");
        var packageArtifact = temp.CreateFile("artifacts", recordingId.ToString("D"), "package.json");
        var guideArtifact = temp.CreateFile("artifacts", classId.ToString("D"), "guide.json");
        await SeedVersionOneDatabaseAsync(
            paths.DatabasePath, classId, recordingId, jobId, videoPath,
            audioArtifact, packageArtifact, guideArtifact);
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);

        await new RecordingDeletionService(database, paths).DeleteAsync(recordingId, default);

        Assert.False(File.Exists(videoPath));
        Assert.False(Directory.Exists(jobDirectory));
        Assert.False(File.Exists(packageArtifact));
        Assert.False(File.Exists(guideArtifact));
        Assert.Empty(await new SqliteLibraryRepository(database).ListRecordingsAsync(null, default));
    }

    [Fact]
    public async Task Delete_prevents_processing_from_starting_after_the_active_job_check()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var classRecord = await repository.CreateClassAsync("Concurrency", null, default);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000008");
        var videoPath = System.IO.Path.Combine(paths.RecordingsRoot, "race.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, classRecord.Id, videoPath, "race.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var request = new ProcessingRequest(
            Guid.Parse("82000000-0000-0000-0000-000000000008"),
            recordingId,
            classRecord.Id,
            videoPath,
            System.IO.Path.Combine(paths.JobsRoot, "82000000-0000-0000-0000-000000000008"),
            false);
        var fileSystem = new CallbackDeletionFileSystem(async cancellationToken =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            try
            {
                await new SqliteProcessingJobStore(database).CreateAsync(request, timeout.Token);
                throw new InvalidOperationException("Processing started while deletion was in progress.");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
            }
        });

        await new RecordingDeletionService(database, paths, fileSystem).DeleteAsync(recordingId, default);

        Assert.Empty(await repository.ListRecordingsAsync(null, default));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "processing_jobs"));
    }

    [Fact]
    public async Task Delete_restores_earlier_files_when_a_later_staging_move_fails()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var classRecord = await repository.CreateClassAsync("Recovery", null, default);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000010");
        var videoPath = temp.CreateFile("recordings", "recover.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, classRecord.Id, videoPath, "recover.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var guidePath = temp.CreateFile("artifacts", classRecord.Id.ToString("D"), "guide.json");
        await SeedClassGuideAsync(database.Connection, classRecord.Id, guidePath);
        var fileSystem = new PhysicalRecordingDeletionFileSystem(
            new FailingMoveFileOperations(2));

        await Assert.ThrowsAsync<IOException>(() =>
            new RecordingDeletionService(database, paths, fileSystem).DeleteAsync(recordingId, default));

        Assert.True(File.Exists(videoPath));
        Assert.True(File.Exists(guidePath));
        Assert.Single(await repository.ListRecordingsAsync(null, default));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "recording_deletion_journal"));
    }

    [Fact]
    public async Task Delete_attempts_to_restore_every_staged_target_when_one_rollback_move_fails()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var classRecord = await repository.CreateClassAsync("Rollback", null, default);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000011");
        var videoPath = temp.CreateFile("recordings", "rollback.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, classRecord.Id, videoPath, "rollback.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var guidePath = temp.CreateFile("artifacts", classRecord.Id.ToString("D"), "guide.json");
        await SeedClassGuideAsync(database.Connection, classRecord.Id, guidePath);
        var recordingArtifact = temp.CreateFile("artifacts", recordingId.ToString("D"), "summary.json");
        var fileSystem = new PhysicalRecordingDeletionFileSystem(
            new FailingMoveFileOperations(3, 4));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new RecordingDeletionService(database, paths, fileSystem).DeleteAsync(recordingId, default));

        Assert.True(File.Exists(videoPath));
        Assert.True(File.Exists(recordingArtifact));
        Assert.Single(await repository.ListRecordingsAsync(null, default));
        Assert.True(await CountRowsAsync(database.Connection, "recording_deletion_journal") > 0);
    }

    [Fact]
    public async Task Startup_recovery_restores_quarantined_files_when_recording_metadata_remains()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000012");
        var videoPath = temp.CreateFile("recordings", "precommit-crash.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, videoPath, "precommit-crash.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var quarantinePath = RecordingDeletionQuarantine.PathFor(videoPath, recordingId);
        File.Move(videoPath, quarantinePath);
        await SeedDeletionJournalAsync(
            database.Connection, recordingId, recordingId, RecordingDeletionTargetKind.Video,
            videoPath, quarantinePath, isDirectory: false);

        await new RecordingDeletionRecoveryService(database, paths).RecoverAsync(default);

        Assert.True(File.Exists(videoPath));
        Assert.False(File.Exists(quarantinePath));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "recording_deletion_journal"));
    }

    [Fact]
    public async Task Startup_recovery_purges_quarantined_files_when_recording_metadata_was_committed_away()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000013");
        var originalPath = System.IO.Path.Combine(paths.RecordingsRoot, "postcommit-crash.mp4");
        var quarantinePath = RecordingDeletionQuarantine.PathFor(originalPath, recordingId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(quarantinePath)!);
        File.WriteAllText(quarantinePath, "pending purge");
        await SeedDeletionJournalAsync(
            database.Connection, recordingId, recordingId, RecordingDeletionTargetKind.Video,
            originalPath, quarantinePath, isDirectory: false);

        await new RecordingDeletionRecoveryService(database, paths).RecoverAsync(default);

        Assert.False(File.Exists(originalPath));
        Assert.False(File.Exists(quarantinePath));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "recording_deletion_journal"));
    }

    [Fact]
    public async Task Startup_recovery_ignores_marker_shaped_files_without_a_registered_intent()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000016");
        var videoPath = temp.CreateFile("recordings", "real.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, videoPath, "real.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var markerShapedFile = temp.CreateFile(
            "recordings",
            $"student-notes{RecordingDeletionQuarantine.Marker}{recordingId:D}");
        var inferredOriginal = markerShapedFile[..markerShapedFile.LastIndexOf(
            RecordingDeletionQuarantine.Marker, StringComparison.Ordinal)];

        await new RecordingDeletionRecoveryService(database, paths).RecoverAsync(default);

        Assert.True(File.Exists(markerShapedFile));
        Assert.False(File.Exists(inferredOriginal));
    }

    [Fact]
    public async Task Startup_recovery_rejects_a_registered_path_outside_its_trusted_root()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000017");
        var outsideOriginal = System.IO.Path.Combine(temp.Path, "outside", "unrelated.mp4");
        var outsideQuarantine = RecordingDeletionQuarantine.PathFor(outsideOriginal, recordingId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outsideQuarantine)!);
        File.WriteAllText(outsideQuarantine, "unrelated");
        await SeedDeletionJournalAsync(
            database.Connection, recordingId, recordingId, RecordingDeletionTargetKind.Video,
            outsideOriginal, outsideQuarantine, isDirectory: false);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RecordingDeletionRecoveryService(database, paths).RecoverAsync(default));

        Assert.True(File.Exists(outsideQuarantine));
        Assert.Equal(1L, await CountRowsAsync(database.Connection, "recording_deletion_journal"));
    }

    [Fact]
    public void Purge_deletes_a_reparse_root_without_following_its_target()
    {
        using var temp = new TestDirectory();
        var targetDirectory = temp.CreateDirectory("outside-target");
        var targetFile = temp.CreateFile("outside-target", "keep.txt");
        var linkPath = System.IO.Path.Combine(temp.Path, "quarantine-link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        RecordingDeletionFileSafety.DeleteDirectoryTree(linkPath);

        Assert.False(Directory.Exists(linkPath));
        Assert.True(File.Exists(targetFile));
    }

    [Fact]
    public async Task Delete_rejects_a_recordings_root_that_is_a_reparse_point()
    {
        using var temp = new TestDirectory();
        var physicalRoot = temp.CreateDirectory("physical-recordings");
        var linkedRoot = System.IO.Path.Combine(temp.Path, "linked-recordings");
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, physicalRoot);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var paths = new LibraryPaths(
            temp.LibraryPaths.DatabasePath,
            temp.LibraryPaths.ArtifactsRoot,
            temp.LibraryPaths.JobsRoot,
            linkedRoot);
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000018");
        var videoPath = System.IO.Path.Combine(linkedRoot, "linked.mp4");
        File.WriteAllText(videoPath, "keep");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, videoPath, "linked.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RecordingDeletionService(database, paths).DeleteAsync(recordingId, default));

        Assert.True(File.Exists(System.IO.Path.Combine(physicalRoot, "linked.mp4")));
        Assert.Single(await repository.ListRecordingsAsync(null, default));
    }

    [Fact]
    public async Task Startup_recovery_retries_a_purge_that_failed_after_metadata_commit()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000014");
        var videoPath = temp.CreateFile("recordings", "purge-retry.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, videoPath, "purge-retry.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var quarantinePath = RecordingDeletionQuarantine.PathFor(videoPath, recordingId);
        var fileSystem = new PhysicalRecordingDeletionFileSystem(new FailingPurgeFileOperations());

        await new RecordingDeletionService(database, paths, fileSystem).DeleteAsync(recordingId, default);

        Assert.False(File.Exists(videoPath));
        Assert.True(File.Exists(quarantinePath));
        Assert.Empty(await repository.ListRecordingsAsync(null, default));

        await new RecordingDeletionRecoveryService(database, paths).RecoverAsync(default);
        Assert.False(File.Exists(quarantinePath));
        Assert.Equal(0L, await CountRowsAsync(database.Connection, "recording_deletion_journal"));
    }

    [Fact]
    public async Task Database_failure_after_staging_restores_the_original_files()
    {
        using var temp = new TestDirectory();
        var paths = temp.LibraryPaths;
        await using var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, default);
        var repository = new SqliteLibraryRepository(database);
        var recordingId = Guid.Parse("81000000-0000-0000-0000-000000000015");
        var videoPath = temp.CreateFile("recordings", "database-rollback.mp4");
        await repository.AddRecordingAsync(new RecordingRecord(
            recordingId, null, videoPath, "database-rollback.mp4", null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"), TimeSpan.FromMinutes(45), 100, true), default);
        var fileSystem = new RemoveMetadataAfterStagingFileSystem(database.Connection);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            new RecordingDeletionService(database, paths, fileSystem).DeleteAsync(recordingId, default));

        Assert.True(File.Exists(videoPath));
        Assert.False(File.Exists(RecordingDeletionQuarantine.PathFor(videoPath, recordingId)));
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

    private static async Task SeedVersionOneDatabaseAsync(
        string databasePath,
        Guid classId,
        Guid recordingId,
        Guid jobId,
        string videoPath,
        string audioArtifact,
        string packageArtifact,
        string guideArtifact)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE schema_info(version INTEGER NOT NULL);
            CREATE TABLE classes(id TEXT PRIMARY KEY, name TEXT NOT NULL, term TEXT, created_at TEXT NOT NULL, is_archived INTEGER NOT NULL);
            CREATE TABLE recordings(id TEXT PRIMARY KEY, class_id TEXT NULL REFERENCES classes(id), file_path TEXT NOT NULL UNIQUE, file_name TEXT NOT NULL, meeting_id TEXT, recorded_at TEXT NOT NULL, duration_ms INTEGER NOT NULL, byte_size INTEGER NOT NULL, video_available INTEGER NOT NULL);
            CREATE TABLE meeting_class_mappings(meeting_id TEXT PRIMARY KEY, class_id TEXT NOT NULL REFERENCES classes(id));
            CREATE TABLE processing_jobs(id TEXT PRIMARY KEY, recording_id TEXT NOT NULL REFERENCES recordings(id), state TEXT NOT NULL, delete_video INTEGER NOT NULL, completed_chunks INTEGER NOT NULL, error_code TEXT, updated_at TEXT NOT NULL);
            CREATE TABLE transcription_chunks(job_id TEXT NOT NULL REFERENCES processing_jobs(id), chunk_index INTEGER NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, sha256 TEXT NOT NULL, artifact_path TEXT, PRIMARY KEY(job_id, chunk_index));
            CREATE TABLE lecture_packages(recording_id TEXT PRIMARY KEY REFERENCES recordings(id), schema_version INTEGER NOT NULL, artifact_path TEXT NOT NULL, source_transcript_hash TEXT NOT NULL, is_stale INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE assignments(id TEXT PRIMARY KEY, recording_id TEXT NOT NULL REFERENCES recordings(id), description TEXT NOT NULL, due_date_text TEXT, due_at TEXT, confidence REAL NOT NULL, is_user_confirmed INTEGER NOT NULL, source_timestamp_ms INTEGER);
            CREATE TABLE class_study_guides(class_id TEXT PRIMARY KEY REFERENCES classes(id), schema_version INTEGER NOT NULL, artifact_path TEXT NOT NULL, is_update_pending INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE app_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO schema_info(version) VALUES (1);
            INSERT INTO classes(id, name, term, created_at, is_archived) VALUES ($classId, 'Legacy class', NULL, $now, 0);
            INSERT INTO recordings(id, class_id, file_path, file_name, meeting_id, recorded_at, duration_ms, byte_size, video_available)
                VALUES ($recordingId, $classId, $video, 'legacy.mp4', NULL, $now, 60000, 100, 1);
            INSERT INTO processing_jobs(id, recording_id, state, delete_video, completed_chunks, error_code, updated_at)
                VALUES ($jobId, $recordingId, 'Completed', 0, 1, NULL, $now);
            INSERT INTO transcription_chunks(job_id, chunk_index, start_ms, end_ms, sha256, artifact_path)
                VALUES ($jobId, 0, 0, 1000, $hash, $audio);
            INSERT INTO lecture_packages(recording_id, schema_version, artifact_path, source_transcript_hash, is_stale, updated_at)
                VALUES ($recordingId, 1, $package, $hash, 0, $now);
            INSERT INTO assignments(id, recording_id, description, due_date_text, due_at, confidence, is_user_confirmed, source_timestamp_ms)
                VALUES ($assignmentId, $recordingId, 'Legacy task', 'Friday', NULL, .8, 0, 100);
            INSERT INTO class_study_guides(class_id, schema_version, artifact_path, is_update_pending, updated_at)
                VALUES ($classId, 1, $guide, 0, $now);
            """;
        command.Parameters.AddWithValue("$classId", classId.ToString("D"));
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$assignmentId", Guid.Parse("83000000-0000-0000-0000-000000000007").ToString("D"));
        command.Parameters.AddWithValue("$now", "2026-08-25T12:00:00.0000000+00:00");
        command.Parameters.AddWithValue("$video", videoPath);
        command.Parameters.AddWithValue("$audio", audioArtifact);
        command.Parameters.AddWithValue("$package", packageArtifact);
        command.Parameters.AddWithValue("$guide", guideArtifact);
        command.Parameters.AddWithValue("$hash", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReplaceArtifactPathAsync(
        SqliteConnection connection,
        string table,
        string path)
    {
        var commandText = table switch
        {
            "audio_chunks" => "UPDATE audio_chunks SET artifact_path = $path;",
            "transcription_chunks" => "UPDATE transcription_chunks SET artifact_path = $path;",
            "processing_transcripts" => "UPDATE processing_transcripts SET artifact_path = $path;",
            "lecture_packages" => "UPDATE lecture_packages SET artifact_path = $path;",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedClassGuideAsync(SqliteConnection connection, Guid classId, string path)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO class_study_guides(
                class_id, schema_version, artifact_path, artifact_sha256, is_update_pending, updated_at)
            VALUES ($classId, 1, $path, $hash, 0, $now);
            """;
        command.Parameters.AddWithValue("$classId", classId.ToString("D"));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$hash", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        command.Parameters.AddWithValue("$now", "2026-08-25T12:00:00.0000000+00:00");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedDeletionJournalAsync(
        SqliteConnection connection,
        Guid recordingId,
        Guid ownerId,
        RecordingDeletionTargetKind kind,
        string originalPath,
        string quarantinePath,
        bool isDirectory)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recording_deletion_journal(
                recording_id, owner_id, target_kind, original_path, quarantine_path, is_directory)
            VALUES ($recordingId, $ownerId, $kind, $originalPath, $quarantinePath, $isDirectory);
            """;
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
        command.Parameters.AddWithValue("$ownerId", ownerId.ToString("D"));
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$originalPath", originalPath);
        command.Parameters.AddWithValue("$quarantinePath", quarantinePath);
        command.Parameters.AddWithValue("$isDirectory", isDirectory ? 1 : 0);
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
                System.IO.Path.Combine(Path, "jobs"),
                System.IO.Path.Combine(Path, "recordings"));
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

    private sealed class CallbackDeletionFileSystem(
        Func<CancellationToken, Task> callback) : IRecordingDeletionFileSystem
    {
        public async Task<IStagedRecordingDeletion> StageAsync(
            Guid recordingId,
            string videoPath,
            string recordingArtifacts,
            IReadOnlyList<string> jobDirectories,
            string? classGuidePath,
            CancellationToken cancellationToken)
        {
            await callback(cancellationToken);
            return new NoopStagedRecordingDeletion();
        }
    }

    private sealed class NoopStagedRecordingDeletion : IStagedRecordingDeletion
    {
        public void Rollback()
        {
        }

        public void Purge()
        {
        }
    }

    private sealed class FailingMoveFileOperations(params int[] failOnMoves) : IRecordingDeletionFileOperations
    {
        private int moveCount;
        private readonly HashSet<int> failures = [.. failOnMoves];

        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void MoveFile(string source, string destination)
        {
            ThrowIfSelectedMove();
            File.Move(source, destination);
        }

        public void MoveDirectory(string source, string destination)
        {
            ThrowIfSelectedMove();
            Directory.Move(source, destination);
        }

        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

        private void ThrowIfSelectedMove()
        {
            moveCount++;
            if (failures.Contains(moveCount))
            {
                throw new IOException("Simulated late staging failure.");
            }
        }
    }

    private sealed class FailingPurgeFileOperations : IRecordingDeletionFileOperations
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void MoveFile(string source, string destination) => File.Move(source, destination);
        public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
        public void DeleteFile(string path) => throw new IOException("Simulated purge failure.");
        public void DeleteDirectory(string path) => throw new IOException("Simulated purge failure.");
    }

    private sealed class RemoveMetadataAfterStagingFileSystem(SqliteConnection connection)
        : IRecordingDeletionFileSystem
    {
        private readonly PhysicalRecordingDeletionFileSystem physical = new();

        public async Task<IStagedRecordingDeletion> StageAsync(
            Guid recordingId,
            string videoPath,
            string recordingArtifacts,
            IReadOnlyList<string> jobDirectories,
            string? classGuidePath,
            CancellationToken cancellationToken)
        {
            var staged = await physical.StageAsync(
                recordingId,
                videoPath,
                recordingArtifacts,
                jobDirectories,
                classGuidePath,
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM recordings WHERE id = $recordingId;";
            command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return staged;
        }
    }
}
