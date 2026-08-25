using Microsoft.Data.Sqlite;
using ZoomRecorder.App.Data;
using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.Data;

public sealed class SqliteProcessingJobStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 16, 30, 0, TimeSpan.Zero);
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task Reopen_round_trips_request_state_hashes_and_deterministic_checkpoint_order()
    {
        using var temp = new TestDirectory();
        ProcessingRequest request;
        await using (var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default))
        {
            request = await RequestAsync(database, temp, "reopen");
            var store = Store(database);
            var job = await store.CreateAsync(request, default);
            job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio, default);
            job = await store.SaveAudioChunksAsync(
                request.JobId,
                job.Revision,
                [Chunk(request, 1, 5_000, 15_000, HashB), Chunk(request, 0, 0, 10_000, HashA)],
                default);
            job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.PreparingAudio, ProcessingState.Transcribing, default);
            job = await store.SaveTranscriptChunkAsync(
                request.JobId,
                job.Revision,
                new TranscriptChunkCheckpoint(1, HashB, Artifact(temp.File("result-1.json"), HashC)),
                default);
            job = await store.SaveTranscriptChunkAsync(
                request.JobId,
                job.Revision,
                new TranscriptChunkCheckpoint(0, HashA, Artifact(temp.File("result-0.json"), HashB)),
                default);
            await store.CommitTranscriptAsync(
                request.JobId,
                job.Revision,
                Artifact(temp.File("transcript.json"), HashA),
                default);
        }

        await using var reopened = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var reopenedStore = Store(reopened);
        var loaded = await reopenedStore.LoadAsync(request.JobId, default);

        Assert.Equal(request, loaded.Request);
        Assert.Equal(ProcessingState.Transcribing, loaded.State);
        Assert.True(loaded.TranscriptCommitted);
        Assert.Equal(Artifact(temp.File("transcript.json"), HashA), loaded.TranscriptArtifact);
        Assert.Equal([0, 1], (await reopenedStore.ListAudioChunksAsync(request.JobId, default)).Select(item => item.Index));
        Assert.Equal([0, 1], (await reopenedStore.ListTranscriptChunksAsync(request.JobId, default)).Select(item => item.Index));
    }

    [Fact]
    public async Task Expected_revision_and_state_make_duplicate_transitions_fail_without_mutation()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "concurrency");
        var store = Store(database);
        var ready = await store.CreateAsync(request, default);

        var preparing = await store.MoveAsync(
            request.JobId, ready.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio, default);

        await Assert.ThrowsAsync<ProcessingConcurrencyException>(() => store.MoveAsync(
            request.JobId, ready.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio, default));
        Assert.Equal(preparing, await store.LoadAsync(request.JobId, default));
    }

    [Fact]
    public async Task CompleteTranscriptOnly_completes_committed_transcript_without_study_artifacts()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "transcript-only");
        var store = Store(database);
        var job = await store.CreateAsync(request, default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio, default);
        job = await store.SaveAudioChunksAsync(request.JobId, job.Revision, [Chunk(request, 0, 0, 10_000, HashA)], default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.PreparingAudio, ProcessingState.Transcribing, default);
        job = await store.CommitTranscriptAsync(request.JobId, job.Revision, Artifact(temp.File("transcript.json"), HashB), default);

        var completed = await store.CompleteTranscriptOnlyAsync(request.JobId, job.Revision, default);

        Assert.Equal(ProcessingState.Completed, completed.State);
        Assert.True(completed.TranscriptCommitted);
        Assert.False(completed.LecturePackageCommitted);
        Assert.False(completed.AssignmentsCommitted);
        Assert.Equal(ClassGuideOutcome.NotAttempted, completed.GuideOutcome);
    }

    [Fact]
    public async Task RestartTranscription_clears_the_stale_final_checkpoint_and_preserves_chunk_checkpoints()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "restart-transcription");
        var store = Store(database);
        var lateStage = await MoveToCommittedStageAsync(
            store, request, temp, ProcessingState.GeneratingStudyPackage);

        var restarted = await store.RestartTranscriptionAsync(
            request.JobId, lateStage.Revision, default);

        Assert.Equal(ProcessingState.Transcribing, restarted.State);
        Assert.False(restarted.TranscriptCommitted);
        Assert.Null(restarted.TranscriptArtifact);
        Assert.Single(await store.ListAudioChunksAsync(request.JobId, default));
    }

    [Fact]
    public async Task CompleteTranscriptOnly_completes_a_transcript_stage_attention_job_and_clears_failure()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "transcript-attention");
        var store = Store(database);
        var job = await store.CreateAsync(request, default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio, default);
        job = await store.SaveAudioChunksAsync(request.JobId, job.Revision, [Chunk(request, 0, 0, 10_000, HashA)], default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.PreparingAudio, ProcessingState.Transcribing, default);
        job = await store.CommitTranscriptAsync(request.JobId, job.Revision, Artifact(temp.File("transcript.json"), HashB), default);
        job = await store.MarkNeedsAttentionAsync(
            request.JobId,
            job.Revision,
            ProcessingState.Transcribing,
            CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed,
            default);

        var completed = await store.CompleteTranscriptOnlyAsync(request.JobId, job.Revision, default);

        Assert.Equal(ProcessingState.Completed, completed.State);
        Assert.Null(completed.FailedStage);
        Assert.Null(completed.ErrorCode);
        Assert.True(completed.TranscriptCommitted);
    }

    [Theory]
    [InlineData(ProcessingState.Transcribing)]
    [InlineData(ProcessingState.GeneratingStudyPackage)]
    [InlineData(ProcessingState.UpdatingClassGuide)]
    public async Task CompleteTranscriptOnly_completes_each_committed_active_late_stage(ProcessingState stage)
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, $"active-{stage}");
        var store = Store(database);
        var job = await MoveToCommittedStageAsync(store, request, temp, stage);

        var completed = await store.CompleteTranscriptOnlyAsync(request.JobId, job.Revision, default);

        Assert.Equal(ProcessingState.Completed, completed.State);
        Assert.True(completed.TranscriptCommitted);
        Assert.Null(completed.FailedStage);
        Assert.Null(completed.ErrorCode);
    }

    [Theory]
    [InlineData(ProcessingState.Transcribing)]
    [InlineData(ProcessingState.GeneratingStudyPackage)]
    [InlineData(ProcessingState.UpdatingClassGuide)]
    public async Task CompleteTranscriptOnly_completes_matching_late_stage_attention_jobs(ProcessingState stage)
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, $"attention-{stage}");
        var store = Store(database);
        var job = await MoveToCommittedStageAsync(store, request, temp, stage);
        job = await store.MarkNeedsAttentionAsync(
            request.JobId,
            job.Revision,
            stage,
            CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed,
            default);

        var completed = await store.CompleteTranscriptOnlyAsync(request.JobId, job.Revision, default);

        Assert.Equal(ProcessingState.Completed, completed.State);
        Assert.Null(completed.FailedStage);
        Assert.Null(completed.ErrorCode);
    }

    [Fact]
    public async Task CompleteTranscriptOnly_rejects_an_uncommitted_transcript_without_mutation()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "uncommitted");
        var store = Store(database);
        var job = await store.CreateAsync(request, default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio, default);
        job = await store.SaveAudioChunksAsync(request.JobId, job.Revision, [Chunk(request, 0, 0, 10_000, HashA)], default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.PreparingAudio, ProcessingState.Transcribing, default);

        await Assert.ThrowsAsync<ProcessingConcurrencyException>(() =>
            store.CompleteTranscriptOnlyAsync(request.JobId, job.Revision, default));

        Assert.Equal(job, await store.LoadAsync(request.JobId, default));
    }

    [Fact]
    public async Task CompleteTranscriptOnly_rejects_a_stale_revision_without_mutation()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "stale");
        var store = Store(database);
        var job = await MoveToCommittedStageAsync(store, request, temp, ProcessingState.Transcribing);

        await Assert.ThrowsAsync<ProcessingConcurrencyException>(() =>
            store.CompleteTranscriptOnlyAsync(request.JobId, job.Revision - 1, default));

        Assert.Equal(job, await store.LoadAsync(request.JobId, default));
    }

    [Fact]
    public async Task CompleteTranscriptOnly_rejects_attention_with_a_mismatched_failed_stage()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "mismatched");
        var store = Store(database);
        var job = await MoveToCommittedStageAsync(store, request, temp, ProcessingState.Transcribing);
        await using (var seed = database.Connection.CreateCommand())
        {
            seed.CommandText = """
                UPDATE processing_jobs
                SET state = 'NeedsAttention', failed_stage = 'PreparingAudio',
                    error_code = 'AudioPreparationFailed', revision = revision + 1
                WHERE id = $jobId;
                """;
            seed.Parameters.AddWithValue("$jobId", request.JobId.ToString("D"));
            await seed.ExecuteNonQueryAsync();
        }
        var mismatched = await store.LoadAsync(request.JobId, default);

        await Assert.ThrowsAsync<ProcessingConcurrencyException>(() =>
            store.CompleteTranscriptOnlyAsync(request.JobId, mismatched.Revision, default));

        Assert.Equal(mismatched, await store.LoadAsync(request.JobId, default));
    }

    [Fact]
    public async Task CompleteTranscriptOnly_preserves_existing_study_and_guide_data()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "preserves-data");
        var store = Store(database);
        var generating = await MoveToCommittedStageAsync(store, request, temp, ProcessingState.GeneratingStudyPackage);
        var package = Artifact(temp.File("package.json"), HashC);
        var committed = await store.CommitLecturePackageAsync(
            request.JobId, generating.Revision, package, HashA, [Assignment()], default);
        var updating = await store.MoveAsync(
            request.JobId,
            committed.Revision,
            ProcessingState.GeneratingStudyPackage,
            ProcessingState.UpdatingClassGuide,
            default);
        var guide = Artifact(temp.File("guide.json"), HashB);
        await using (var seed = database.Connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO class_study_guides(
                    class_id, schema_version, artifact_path, artifact_sha256,
                    is_update_pending, updated_at)
                VALUES ($classId, 1, $path, $hash, 0, $updatedAt);
                """;
            seed.Parameters.AddWithValue("$classId", request.ClassId.ToString("D"));
            seed.Parameters.AddWithValue("$path", guide.Path);
            seed.Parameters.AddWithValue("$hash", guide.Sha256);
            seed.Parameters.AddWithValue("$updatedAt", Now.ToString("O"));
            await seed.ExecuteNonQueryAsync();
        }

        var completed = await store.CompleteTranscriptOnlyAsync(request.JobId, updating.Revision, default);

        Assert.Equal(ProcessingState.Completed, completed.State);
        Assert.True(completed.LecturePackageCommitted);
        Assert.True(completed.AssignmentsCommitted);
        await using var verify = database.Connection.CreateCommand();
        verify.CommandText = """
            SELECT
                (SELECT artifact_path FROM lecture_packages WHERE recording_id = $recordingId),
                (SELECT COUNT(*) FROM assignments WHERE recording_id = $recordingId),
                (SELECT artifact_path FROM class_study_guides WHERE class_id = $classId);
            """;
        verify.Parameters.AddWithValue("$recordingId", request.RecordingId.ToString("D"));
        verify.Parameters.AddWithValue("$classId", request.ClassId.ToString("D"));
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(package.Path, reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(guide.Path, reader.GetString(2));
    }

    [Fact]
    public async Task Package_and_assignments_roll_back_together_on_late_failure()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "rollback");
        var store = Store(database);
        var generating = await MoveToGeneratingAsync(store, request, temp);
        var previousPath = temp.File("previous-package.json");
        await using (var seed = database.Connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO lecture_packages(
                    recording_id, schema_version, artifact_path, artifact_sha256,
                    source_transcript_hash, is_stale, updated_at)
                VALUES ($recordingId, 1, $path, $hash, $sourceHash, 0, $updatedAt);
                CREATE TRIGGER fail_assignment_insert
                BEFORE INSERT ON assignments
                BEGIN
                    SELECT RAISE(ABORT, 'raw sqlite failure');
                END;
                """;
            seed.Parameters.AddWithValue("$recordingId", request.RecordingId.ToString("D"));
            seed.Parameters.AddWithValue("$path", previousPath);
            seed.Parameters.AddWithValue("$hash", HashA);
            seed.Parameters.AddWithValue("$sourceHash", HashB);
            seed.Parameters.AddWithValue("$updatedAt", Now.ToString("O"));
            await seed.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => store.CommitLecturePackageAsync(
            request.JobId,
            generating.Revision,
            Artifact(temp.File("new-package.json"), HashC),
            HashA,
            [Assignment()],
            default));

        var loaded = await store.LoadAsync(request.JobId, default);
        Assert.False(loaded.LecturePackageCommitted);
        Assert.False(loaded.AssignmentsCommitted);
        await using var verify = database.Connection.CreateCommand();
        verify.CommandText = """
            SELECT artifact_path, artifact_sha256,
                   (SELECT COUNT(*) FROM assignments WHERE recording_id = $recordingId)
            FROM lecture_packages WHERE recording_id = $recordingId;
            """;
        verify.Parameters.AddWithValue("$recordingId", request.RecordingId.ToString("D"));
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(previousPath, reader.GetString(0));
        Assert.Equal(HashA, reader.GetString(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Fact]
    public async Task Guide_pending_completes_job_and_can_be_retried_without_losing_the_explicit_outcome()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "guide");
        var store = Store(database);
        var generating = await MoveToGeneratingAsync(store, request, temp);
        var package = Artifact(temp.File("package.json"), HashC);
        var committed = await store.CommitLecturePackageAsync(
            request.JobId, generating.Revision, package, HashA, [Assignment()], default);
        var updating = await store.MoveAsync(
            request.JobId,
            committed.Revision,
            ProcessingState.GeneratingStudyPackage,
            ProcessingState.UpdatingClassGuide,
            default);

        var pending = await store.CompleteGuideAsync(
            request.JobId, updating.Revision, ClassGuideOutcome.Pending, guide: null, default);

        Assert.Equal(ProcessingState.Completed, pending.State);
        Assert.True(pending.GuideUpdatePending);
        Assert.True(pending.IsDeletionEligible);

        var guide = Artifact(temp.File("guide.json"), HashB);
        var succeeded = await store.CompleteGuideAsync(
            request.JobId, pending.Revision, ClassGuideOutcome.Succeeded, guide, default);
        Assert.Equal(ClassGuideOutcome.Succeeded, succeeded.GuideOutcome);
        await using var verify = database.Connection.CreateCommand();
        verify.CommandText = "SELECT artifact_path, artifact_sha256, is_update_pending FROM class_study_guides WHERE class_id = $classId;";
        verify.Parameters.AddWithValue("$classId", request.ClassId.ToString("D"));
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(guide.Path, reader.GetString(0));
        Assert.Equal(guide.Sha256, reader.GetString(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Fact]
    public async Task Resumable_jobs_are_active_or_needs_attention_only_and_are_deterministically_ordered()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var store = Store(database);
        var readyRequest = await RequestAsync(database, temp, "ready");
        var attentionRequest = await RequestAsync(database, temp, "attention");
        var cancelledRequest = await RequestAsync(database, temp, "cancelled");
        var ready = await store.CreateAsync(readyRequest, default);
        var attention = await store.CreateAsync(attentionRequest, default);
        var cancelled = await store.CreateAsync(cancelledRequest, default);
        await store.MarkNeedsAttentionAsync(
            attentionRequest.JobId,
            attention.Revision,
            ProcessingState.ReadyToProcess,
            CloudProcessingErrorCode.AudioPreparationFailed,
            default);
        await store.CancelAsync(cancelledRequest.JobId, cancelled.Revision, default);

        var resumable = await store.ListResumableAsync(default);

        Assert.Equal(
            new[] { readyRequest.JobId, attentionRequest.JobId }.OrderBy(id => id),
            resumable.Select(item => item.Request.JobId));
        Assert.DoesNotContain(resumable, item => item.Request.JobId == cancelledRequest.JobId);
        Assert.Equal(ready.Revision, (await store.LoadAsync(readyRequest.JobId, default)).Revision);
    }

    [Fact]
    public async Task Job_foreign_keys_reject_missing_recording_or_class_ids()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var store = Store(database);
        var request = new ProcessingRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            temp.File("missing.mp4"),
            temp.File("missing-job"),
            false);

        await Assert.ThrowsAsync<SqliteException>(() => store.CreateAsync(request, default));
    }

    [Fact]
    public async Task Audio_chunk_paths_must_be_direct_children_of_the_registered_job_directory()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var request = await RequestAsync(database, temp, "audio-containment");
        var store = Store(database);
        var ready = await store.CreateAsync(request, default);
        var preparing = await store.MoveAsync(
            request.JobId, ready.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio, default);
        var invalidPaths = new[]
        {
            Path.Combine(request.JobDirectory, "nested", "chunk-0.m4a"),
            Path.Combine(Path.GetDirectoryName(request.JobDirectory)!, "sibling", "chunk-0.m4a")
        };

        foreach (var invalidPath in invalidPaths)
        {
            var chunk = new AudioChunk(0, invalidPath, 0, 10_000, HashA, 100);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.SaveAudioChunksAsync(request.JobId, preparing.Revision, [chunk], default));
        }

        Assert.Empty(await store.ListAudioChunksAsync(request.JobId, default));
        Assert.Equal(preparing.Revision, (await store.LoadAsync(request.JobId, default)).Revision);
    }

    [Fact]
    public async Task Version_one_migration_preserves_legacy_jobs_chunks_packages_assignments_and_guides()
    {
        using var temp = new TestDirectory();
        var classId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await SeedVersionOneDatabaseAsync(temp.DatabasePath, classId, recordingId, jobId);

        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        await using var command = database.Connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT version FROM schema_info),
                (SELECT completed_chunks FROM processing_jobs WHERE id = $jobId),
                (SELECT COUNT(*) FROM audio_chunks WHERE job_id = $jobId AND chunk_index = 0 AND artifact_path = $audioPath),
                (SELECT COUNT(*) FROM lecture_packages WHERE recording_id = $recordingId AND artifact_path = $packagePath),
                (SELECT COUNT(*) FROM assignments WHERE recording_id = $recordingId AND description = 'Legacy assignment'),
                (SELECT COUNT(*) FROM class_study_guides WHERE class_id = $classId AND artifact_path = $guidePath),
                (SELECT artifact_sha256 FROM lecture_packages WHERE recording_id = $recordingId),
                (SELECT artifact_sha256 FROM class_study_guides WHERE class_id = $classId);
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
        command.Parameters.AddWithValue("$classId", classId.ToString("D"));
        command.Parameters.AddWithValue("$audioPath", temp.File("legacy-chunk.m4a"));
        command.Parameters.AddWithValue("$packagePath", temp.File("legacy-package.json"));
        command.Parameters.AddWithValue("$guidePath", temp.File("legacy-guide.json"));
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(new string('0', 64), reader.GetString(6));
        Assert.Equal(new string('0', 64), reader.GetString(7));
    }

    private static async Task<ProcessingJobSnapshot> MoveToGeneratingAsync(
        SqliteProcessingJobStore store,
        ProcessingRequest request,
        TestDirectory temp)
    {
        var job = await store.CreateAsync(request, default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio, default);
        job = await store.SaveAudioChunksAsync(
            request.JobId,
            job.Revision,
            [Chunk(request, 0, 0, 10_000, HashA)],
            default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.PreparingAudio, ProcessingState.Transcribing, default);
        job = await store.CommitTranscriptAsync(
            request.JobId,
            job.Revision,
            Artifact(temp.File("transcript.json"), HashB),
            default);
        return await store.MoveAsync(
            request.JobId,
            job.Revision,
            ProcessingState.Transcribing,
            ProcessingState.GeneratingStudyPackage,
            default);
    }

    private static async Task<ProcessingJobSnapshot> MoveToCommittedStageAsync(
        SqliteProcessingJobStore store,
        ProcessingRequest request,
        TestDirectory temp,
        ProcessingState stage)
    {
        var job = await store.CreateAsync(request, default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio, default);
        job = await store.SaveAudioChunksAsync(
            request.JobId,
            job.Revision,
            [Chunk(request, 0, 0, 10_000, HashA)],
            default);
        job = await store.MoveAsync(request.JobId, job.Revision, ProcessingState.PreparingAudio, ProcessingState.Transcribing, default);
        job = await store.CommitTranscriptAsync(
            request.JobId,
            job.Revision,
            Artifact(temp.File("transcript.json"), HashB),
            default);
        if (stage == ProcessingState.Transcribing)
        {
            return job;
        }

        job = await store.MoveAsync(
            request.JobId,
            job.Revision,
            ProcessingState.Transcribing,
            ProcessingState.GeneratingStudyPackage,
            default);
        if (stage == ProcessingState.GeneratingStudyPackage)
        {
            return job;
        }

        if (stage != ProcessingState.UpdatingClassGuide)
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        job = await store.CommitLecturePackageAsync(
            request.JobId,
            job.Revision,
            Artifact(temp.File("package.json"), HashC),
            HashA,
            [Assignment()],
            default);
        return await store.MoveAsync(
            request.JobId,
            job.Revision,
            ProcessingState.GeneratingStudyPackage,
            ProcessingState.UpdatingClassGuide,
            default);
    }

    private static async Task<ProcessingRequest> RequestAsync(
        LibraryDatabase database,
        TestDirectory temp,
        string suffix)
    {
        var repository = new SqliteLibraryRepository(database, () => Now);
        var classRecord = await repository.CreateClassAsync($"Class {suffix}", null, default);
        var recordingId = Guid.NewGuid();
        var mp4Path = temp.File($"{suffix}.mp4");
        await repository.AddRecordingAsync(
            new RecordingRecord(
                recordingId,
                classRecord.Id,
                mp4Path,
                Path.GetFileName(mp4Path),
                null,
                Now,
                TimeSpan.FromMinutes(5),
                1234,
                true),
            default);
        return new ProcessingRequest(
            Guid.NewGuid(), recordingId, classRecord.Id, mp4Path, temp.File($"job-{suffix}"), true);
    }

    private static SqliteProcessingJobStore Store(LibraryDatabase database) => new(database, () => Now);

    private static async Task SeedVersionOneDatabaseAsync(
        string databasePath,
        Guid classId,
        Guid recordingId,
        Guid jobId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
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
                VALUES ($recordingId, $classId, $mp4Path, 'legacy.mp4', NULL, $now, 60000, 1234, 1);
            INSERT INTO processing_jobs(id, recording_id, state, delete_video, completed_chunks, error_code, updated_at)
                VALUES ($jobId, $recordingId, 'Transcribing', 1, 1, NULL, $now);
            INSERT INTO transcription_chunks(job_id, chunk_index, start_ms, end_ms, sha256, artifact_path)
                VALUES ($jobId, 0, 0, 10000, $hash, $audioPath);
            INSERT INTO lecture_packages(recording_id, schema_version, artifact_path, source_transcript_hash, is_stale, updated_at)
                VALUES ($recordingId, 1, $packagePath, $hash, 0, $now);
            INSERT INTO assignments(id, recording_id, description, due_date_text, due_at, confidence, is_user_confirmed, source_timestamp_ms)
                VALUES ($assignmentId, $recordingId, 'Legacy assignment', 'Friday', NULL, 0.8, 0, 100);
            INSERT INTO class_study_guides(class_id, schema_version, artifact_path, is_update_pending, updated_at)
                VALUES ($classId, 1, $guidePath, 0, $now);
            """;
        command.Parameters.AddWithValue("$classId", classId.ToString("D"));
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("D"));
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$assignmentId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$now", Now.ToString("O"));
        command.Parameters.AddWithValue("$mp4Path", Path.GetFullPath(Path.Combine(Path.GetDirectoryName(databasePath)!, "legacy.mp4")));
        command.Parameters.AddWithValue("$audioPath", Path.GetFullPath(Path.Combine(Path.GetDirectoryName(databasePath)!, "..", "legacy-chunk.m4a")));
        command.Parameters.AddWithValue("$packagePath", Path.GetFullPath(Path.Combine(Path.GetDirectoryName(databasePath)!, "..", "legacy-package.json")));
        command.Parameters.AddWithValue("$guidePath", Path.GetFullPath(Path.Combine(Path.GetDirectoryName(databasePath)!, "..", "legacy-guide.json")));
        command.Parameters.AddWithValue("$hash", HashA);
        await command.ExecuteNonQueryAsync();
    }

    private static AudioChunk Chunk(
        ProcessingRequest request,
        int index,
        long start,
        long end,
        string hash) =>
        new(index, Path.Combine(request.JobDirectory, $"chunk-{index}.m4a"), start, end, hash, 100 + index);

    private static ArtifactCheckpoint Artifact(string path, string hash) => new(Path.GetFullPath(path), hash);

    private static StudyAssignment Assignment() =>
        new("Read chapter", "Friday", new DateOnly(2026, 8, 21), 0.75, new TimestampReference(100, 200));

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZoomRecorder.Tests", Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string DatabasePath => System.IO.Path.Combine(Path, "nested", "library.db");
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
