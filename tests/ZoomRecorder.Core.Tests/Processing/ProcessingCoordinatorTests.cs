using System.Security.Cryptography;
using System.Text.Json;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.Core.Tests.Processing;

public sealed class ProcessingCoordinatorTests
{
    private static readonly Guid JobId = Guid.Parse("81000000-0000-0000-0000-000000000001");
    private static readonly Guid RecordingId = Guid.Parse("81000000-0000-0000-0000-000000000002");
    private static readonly Guid ClassId = Guid.Parse("81000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Happy_path_commits_every_checkpoint_and_reports_completed_progress()
    {
        using var fixture = new Fixture();
        var progress = new List<ProcessingProgress>();
        fixture.Coordinator.ProgressChanged += (_, item) => progress.Add(item);

        await fixture.Coordinator.StartAsync(fixture.Request, default);

        var job = await fixture.Store.LoadAsync(JobId, default);
        Assert.Equal(ProcessingState.Completed, job.State);
        Assert.True(job.TranscriptCommitted);
        Assert.False(job.LecturePackageCommitted);
        Assert.False(job.AssignmentsCommitted);
        Assert.Equal(ClassGuideOutcome.NotAttempted, job.GuideOutcome);
        Assert.Equal([0, 1], fixture.Transcriber.RequestedChunkIndexes);
        Assert.Equal(0, fixture.Generator.LectureCalls);
        Assert.Equal(0, fixture.Generator.GuideCalls);
        Assert.Equal(0, fixture.Recycler.Calls);
        Assert.Equal(
            [
                ProcessingState.PreparingAudio,
                ProcessingState.Transcribing,
                ProcessingState.Completed
            ],
            progress.Select(item => item.State).Distinct());
        Assert.False(progress[^1].GuideUpdatePending);
    }

    [Fact]
    public async Task Transcript_only_run_stops_after_transcript_and_preserves_video()
    {
        using var fixture = new Fixture();

        await fixture.Coordinator.StartAsync(fixture.Request, CancellationToken.None);

        var job = await fixture.Store.LoadAsync(JobId, default);
        Assert.Equal(ProcessingState.Completed, job.State);
        Assert.True(job.TranscriptCommitted);
        Assert.Equal(0, fixture.Generator.LectureCalls);
        Assert.Equal(0, fixture.Generator.GuideCalls);
    }

    [Fact]
    public async Task Transcription_activity_is_published_for_its_own_job()
    {
        using var fixture = new Fixture();
        var progress = new List<ProcessingProgress>();
        fixture.Coordinator.ProgressChanged += (_, item) => progress.Add(item);

        await fixture.Coordinator.StartAsync(fixture.Request, CancellationToken.None);

        var activity = progress.First(item => item.TranscriptionActivity is not null);
        Assert.Equal(JobId, activity.JobId);
        Assert.Equal(TranscriptionActivityKind.Transcribing, activity.TranscriptionActivity!.Kind);
        Assert.Equal(12, activity.ActivityCompletedBytes);
        Assert.Equal(24, activity.ActivityTotalBytes);
    }

    [Fact]
    public async Task Transcript_only_completion_finishes_publication_and_cleanup_after_caller_cancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var progress = new List<ProcessingState>();
        fixture.Coordinator.ProgressChanged += (_, item) => progress.Add(item.State);
        fixture.Store.OnCompleteTranscriptOnly = cancellation.Cancel;

        await fixture.Coordinator.StartAsync(fixture.Request, cancellation.Token);

        Assert.Equal(ProcessingState.Completed, (await fixture.Store.LoadAsync(JobId, default)).State);
        Assert.Contains(ProcessingState.Completed, progress);
        Assert.Equal([fixture.Request.JobDirectory], fixture.Artifacts.CleanupDirectories);
    }

    [Theory]
    [InlineData(ProcessingState.GeneratingStudyPackage)]
    [InlineData(ProcessingState.UpdatingClassGuide)]
    public async Task Resume_completes_historical_late_stage_jobs_without_study_generation(ProcessingState stage)
    {
        using var fixture = new Fixture();
        await fixture.Store.CreateAsync(fixture.Request, default);
        fixture.Store.SeedHistoricalLateStage(stage);

        await fixture.Coordinator.ResumeAsync(JobId, default);

        Assert.Equal(ProcessingState.Completed, (await fixture.Store.LoadAsync(JobId, default)).State);
        Assert.Equal(0, fixture.Generator.LectureCalls);
        Assert.Equal(0, fixture.Generator.GuideCalls);
        Assert.Equal(0, fixture.Recycler.Calls);
    }

    [Fact]
    public async Task Resume_reuses_every_completed_transcript_chunk()
    {
        using var fixture = new Fixture();
        await fixture.Coordinator.StartAsync(fixture.Request, default);
        fixture.Store.RewindToTranscribing(clearFinalCheckpoints: true);
        fixture.Transcriber.ResetRequests();

        await fixture.Coordinator.ResumeAsync(JobId, default);

        Assert.Empty(fixture.Transcriber.RequestedChunkIndexes);
    }

    [Fact]
    public async Task Resume_retranscribes_only_the_chunk_with_a_corrupt_result_artifact()
    {
        using var fixture = new Fixture();
        await fixture.Coordinator.StartAsync(fixture.Request, default);
        fixture.Store.RewindToTranscribing(clearFinalCheckpoints: true);
        var checkpoint = Assert.Single(
            await fixture.Store.ListTranscriptChunksAsync(JobId, default),
            item => item.Index == 1);
        fixture.Artifacts.Corrupt(checkpoint.Artifact.Path);
        fixture.Transcriber.ResetRequests();

        await fixture.Coordinator.ResumeAsync(JobId, default);

        Assert.Equal([1], fixture.Transcriber.RequestedChunkIndexes);
    }

    [Fact]
    public async Task Resume_reprepares_corrupt_audio_and_retranscribes_only_the_changed_chunk_hash()
    {
        using var fixture = new Fixture();
        await fixture.Coordinator.StartAsync(fixture.Request, default);
        fixture.Store.RewindToTranscribing(clearFinalCheckpoints: true);
        var corrupt = Assert.Single(
            await fixture.Store.ListAudioChunksAsync(JobId, default),
            item => item.Index == 1);
        fixture.Artifacts.Corrupt(corrupt.Path);
        fixture.Audio.ChangedIndex = 1;
        fixture.Transcriber.ResetRequests();

        await fixture.Coordinator.ResumeAsync(JobId, default);

        Assert.Equal([1], fixture.Transcriber.RequestedChunkIndexes);
    }

    [Fact]
    public async Task Resume_retranscribes_only_a_hash_valid_result_with_invalid_chunk_identity()
    {
        using var fixture = new Fixture();
        await fixture.Coordinator.StartAsync(fixture.Request, default);
        fixture.Store.RewindToTranscribing(clearFinalCheckpoints: true);
        var checkpoint = Assert.Single(
            await fixture.Store.ListTranscriptChunksAsync(JobId, default),
            item => item.Index == 1);
        var replaced = fixture.Artifacts.Replace(
            checkpoint.Artifact.Path,
            new TranscriptChunk(99, 5_000, 15_000, [new TranscriptSegment(5_000, 15_000, "wrong")]));
        fixture.Store.ReplaceTranscriptArtifact(1, replaced);
        fixture.Transcriber.ResetRequests();

        await fixture.Coordinator.ResumeAsync(JobId, default);

        Assert.Equal([1], fixture.Transcriber.RequestedChunkIndexes);
    }

    [Fact]
    public async Task Resume_after_late_transcription_failure_does_not_repeat_the_successful_paid_call()
    {
        using var fixture = new Fixture();
        fixture.Transcriber.FailIndex = 1;

        var failure = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            fixture.Coordinator.StartAsync(fixture.Request, default));

        Assert.Equal(CloudProcessingErrorCode.TranscriptionUnavailable, failure.Code);
        var failed = await fixture.Store.LoadAsync(JobId, default);
        Assert.Equal(ProcessingState.NeedsAttention, failed.State);
        Assert.Equal(ProcessingState.Transcribing, failed.FailedStage);
        Assert.Equal(CloudProcessingErrorCode.TranscriptionUnavailable, failed.ErrorCode);

        fixture.Transcriber.FailIndex = null;
        await fixture.Coordinator.ResumeAsync(JobId, default);

        Assert.Equal([0, 1, 1], fixture.Transcriber.RequestedChunkIndexes);
        Assert.True(File.Exists(fixture.Request.Mp4Path));
    }

    [Fact]
    public async Task Cloud_era_transcribing_attention_job_resumes_from_existing_m4a_without_cloud_calls()
    {
        using var fixture = new Fixture();
        fixture.Transcriber.FailIndex = 0;
        await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            fixture.Coordinator.StartAsync(fixture.Request, CancellationToken.None));
        var checkpointedAudio = await fixture.Store.ListAudioChunksAsync(JobId, CancellationToken.None);
        Assert.All(checkpointedAudio, chunk => Assert.EndsWith(".m4a", chunk.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, fixture.Audio.Calls);

        fixture.Transcriber.FailIndex = null;
        await fixture.Coordinator.ResumeAsync(JobId, CancellationToken.None);

        Assert.Equal(1, fixture.Audio.Calls);
        Assert.Equal([0, 0, 1], fixture.Transcriber.RequestedChunkIndexes);
        Assert.Equal(0, fixture.Generator.LectureCalls);
        Assert.Equal(0, fixture.Generator.GuideCalls);
        Assert.True(File.Exists(fixture.Request.Mp4Path));
    }

    [Fact]
    public async Task Audio_preparer_paths_must_be_direct_children_of_the_registered_job_directory()
    {
        using var fixture = new Fixture();
        fixture.Audio.ReturnedDirectory = Path.Combine(Path.GetDirectoryName(fixture.Request.JobDirectory)!, "outside-job");

        var failure = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            fixture.Coordinator.StartAsync(fixture.Request, default));

        Assert.Equal(CloudProcessingErrorCode.AudioPreparationFailed, failure.Code);
        Assert.Empty(await fixture.Store.ListAudioChunksAsync(JobId, default));
        var failed = await fixture.Store.LoadAsync(JobId, default);
        Assert.Equal(ProcessingState.NeedsAttention, failed.State);
        Assert.Equal(ProcessingState.PreparingAudio, failed.FailedStage);
    }

    [Fact]
    public async Task Progress_subscriber_exceptions_are_isolated_from_processing_and_other_subscribers()
    {
        using var fixture = new Fixture();
        var observed = new List<ProcessingState>();
        fixture.Coordinator.ProgressChanged += (_, _) => throw new InvalidOperationException("subscriber secret");
        fixture.Coordinator.ProgressChanged += (_, progress) => observed.Add(progress.State);

        await fixture.Coordinator.StartAsync(fixture.Request, default);

        Assert.Equal(ProcessingState.Completed, (await fixture.Store.LoadAsync(JobId, default)).State);
        Assert.Contains(ProcessingState.Completed, observed);
    }

    [Fact]
    public async Task Needs_attention_persistence_failure_is_sanitized_and_does_not_expose_raw_storage_errors()
    {
        using var fixture = new Fixture();
        fixture.Transcriber.FailIndex = 0;
        fixture.Store.FailMarkNeedsAttention = true;

        var failure = await Assert.ThrowsAsync<ProcessingOperationException>(() =>
            fixture.Coordinator.StartAsync(fixture.Request, default));

        Assert.Equal(CloudProcessingErrorCode.StorageCommitFailed, failure.Code);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ProcessingState.Transcribing, (await fixture.Store.LoadAsync(JobId, default)).State);
    }

    [Theory]
    [InlineData(ProcessingState.PreparingAudio)]
    [InlineData(ProcessingState.Transcribing)]
    public async Task Caller_cancellation_at_each_external_stage_records_cancelled_and_preserves_mp4(
        ProcessingState stage)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.CancelAt(stage, cancellation);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Coordinator.StartAsync(fixture.Request, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(ProcessingState.Cancelled, (await fixture.Store.LoadAsync(JobId, default)).State);
        Assert.True(File.Exists(fixture.Request.Mp4Path));
        Assert.Equal([fixture.Request.JobDirectory], fixture.Artifacts.CleanupDirectories);
    }

    [Fact]
    public async Task Cancellation_during_atomic_write_finishes_the_checkpoint_before_recording_cancelled()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Artifacts.OnBeforeWrite = name =>
        {
            if (name.Contains("transcript-chunk-000000", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Coordinator.StartAsync(fixture.Request, cancellation.Token));

        Assert.Single(await fixture.Store.ListTranscriptChunksAsync(JobId, default));
        Assert.Equal(ProcessingState.Cancelled, (await fixture.Store.LoadAsync(JobId, default)).State);
        Assert.True(File.Exists(fixture.Request.Mp4Path));
    }

    [Fact]
    public async Task Explicit_cancel_waits_for_an_inflight_durable_write()
    {
        using var fixture = new Fixture();
        fixture.Artifacts.BlockWriteContaining = "transcript-chunk-000000";
        var processing = fixture.Coordinator.StartAsync(fixture.Request, default);
        await fixture.Artifacts.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelling = fixture.Coordinator.CancelAsync(JobId, default);
        Assert.False(cancelling.IsCompleted);

        fixture.Artifacts.ReleaseBlockedWrite.TrySetResult();
        await cancelling;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.Single(await fixture.Store.ListTranscriptChunksAsync(JobId, default));
        Assert.Equal(ProcessingState.Cancelled, (await fixture.Store.LoadAsync(JobId, default)).State);
    }

    [Fact]
    public async Task Duplicate_resume_is_rejected_while_the_first_operation_is_active()
    {
        using var fixture = new Fixture();
        fixture.Artifacts.BlockWriteContaining = "transcript-chunk-000000";
        var processing = fixture.Coordinator.StartAsync(fixture.Request, default);
        await fixture.Artifacts.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ProcessingConcurrencyException>(() =>
            fixture.Coordinator.ResumeAsync(JobId, default));

        fixture.Artifacts.ReleaseBlockedWrite.TrySetResult();
        await processing;
    }

    [Fact]
    public async Task Startup_recovery_cleans_only_database_registered_job_directories()
    {
        using var fixture = new Fixture();
        await fixture.Store.CreateAsync(fixture.Request, default);

        var resumable = await fixture.Coordinator.RecoverAsync(default);

        Assert.Equal(JobId, Assert.Single(resumable).Request.JobId);
        Assert.Equal([fixture.Request.JobDirectory], fixture.Artifacts.CleanupDirectories);
        Assert.DoesNotContain(Path.Combine(Path.GetTempPath(), "unregistered-job"), fixture.Artifacts.CleanupDirectories);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string tempPath;

        internal Fixture()
        {
            tempPath = Path.Combine(Path.GetTempPath(), "ZoomRecorder.Tests", Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(tempPath);
            var mp4Path = Path.Combine(tempPath, "lecture.mp4");
            File.WriteAllBytes(mp4Path, [1, 2, 3]);
            Request = new ProcessingRequest(
                JobId,
                RecordingId,
                ClassId,
                mp4Path,
                Path.Combine(tempPath, "job"),
                DeleteVideoAfterSuccess: true);
            Artifacts = new FakeArtifactStore();
            Store = new FakeProcessingJobStore(Now);
            Audio = new FakeAudioChunkPreparer(Artifacts, Request.JobDirectory);
            Transcriber = new FakeTranscriptionClient();
            Generator = new FakeStudyGenerationClient();
            Recycler = new FakeVideoRecycler();
            VideoDeletion = new FakeVideoDeletionStore();
            Coordinator = new ProcessingCoordinator(
                Store,
                Audio,
                Transcriber,
                Generator,
                Artifacts,
                maxChunkBytes: 24 * 1024 * 1024,
                Recycler,
                VideoDeletion);
        }

        internal ProcessingRequest Request { get; }
        internal FakeProcessingJobStore Store { get; }
        internal FakeArtifactStore Artifacts { get; }
        internal FakeAudioChunkPreparer Audio { get; }
        internal FakeTranscriptionClient Transcriber { get; }
        internal FakeStudyGenerationClient Generator { get; }
        internal FakeVideoRecycler Recycler { get; }
        internal FakeVideoDeletionStore VideoDeletion { get; }
        internal ProcessingCoordinator Coordinator { get; }

        internal void CancelAt(ProcessingState stage, CancellationTokenSource cancellation)
        {
            switch (stage)
            {
                case ProcessingState.PreparingAudio:
                    Audio.OnCall = cancellation.Cancel;
                    break;
                case ProcessingState.Transcribing:
                    Transcriber.OnCall = cancellation.Cancel;
                    break;
                case ProcessingState.GeneratingStudyPackage:
                    Generator.OnLecture = cancellation.Cancel;
                    break;
                case ProcessingState.UpdatingClassGuide:
                    Generator.OnGuide = cancellation.Cancel;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stage));
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }

    private sealed class FakeProcessingJobStore(DateTimeOffset now) : IProcessingJobStore
    {
        private ProcessingJobSnapshot? job;
        private IReadOnlyList<AudioChunk> audioChunks = [];
        private readonly Dictionary<int, TranscriptChunkCheckpoint> transcriptChunks = [];
        private readonly List<ArtifactCheckpoint> lecturePackages = [];

        internal bool FailMarkNeedsAttention { get; set; }
        internal Action? OnCompleteTranscriptOnly { get; set; }

        public Task<ProcessingJobSnapshot> CreateAsync(ProcessingRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (job is not null)
            {
                throw new ProcessingConcurrencyException(request.JobId);
            }

            job = new ProcessingJobSnapshot(
                request,
                ProcessingState.ReadyToProcess,
                FailedStage: null,
                ErrorCode: null,
                TranscriptCommitted: false,
                TranscriptArtifact: null,
                LecturePackageCommitted: false,
                LecturePackageArtifact: null,
                AssignmentsCommitted: false,
                ClassGuideOutcome.NotAttempted,
                Revision: 0,
                now);
            return Task.FromResult(job);
        }

        public Task<ProcessingJobSnapshot> LoadAsync(Guid jobId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Required(jobId));
        }

        public Task<ProcessingJobSnapshot> MoveAsync(
            Guid jobId,
            long expectedRevision,
            ProcessingState expectedState,
            ProcessingState nextState,
            CancellationToken cancellationToken) =>
            Mutate(jobId, expectedRevision, expectedState, current => current with
            {
                State = nextState,
                FailedStage = null,
                ErrorCode = null
            }, cancellationToken);

        public Task<ProcessingJobSnapshot> SaveAudioChunksAsync(
            Guid jobId,
            long expectedRevision,
            IReadOnlyList<AudioChunk> chunks,
            CancellationToken cancellationToken)
        {
            audioChunks = chunks.ToArray();
            return Mutate(jobId, expectedRevision, Required(jobId).State, current => current, cancellationToken);
        }

        public Task<IReadOnlyList<AudioChunk>> ListAudioChunksAsync(Guid jobId, CancellationToken cancellationToken)
        {
            _ = Required(jobId);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(audioChunks);
        }

        public Task<ProcessingJobSnapshot> SaveTranscriptChunkAsync(
            Guid jobId,
            long expectedRevision,
            TranscriptChunkCheckpoint chunk,
            CancellationToken cancellationToken)
        {
            transcriptChunks[chunk.Index] = chunk;
            return Mutate(jobId, expectedRevision, ProcessingState.Transcribing, current => current, cancellationToken);
        }

        public Task<IReadOnlyList<TranscriptChunkCheckpoint>> ListTranscriptChunksAsync(
            Guid jobId,
            CancellationToken cancellationToken)
        {
            _ = Required(jobId);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TranscriptChunkCheckpoint>>(
                transcriptChunks.Values.OrderBy(item => item.Index).ToArray());
        }

        public Task<ProcessingJobSnapshot> CommitTranscriptAsync(
            Guid jobId,
            long expectedRevision,
            ArtifactCheckpoint transcript,
            CancellationToken cancellationToken) =>
            Mutate(jobId, expectedRevision, ProcessingState.Transcribing, current => current with
            {
                TranscriptCommitted = true,
                TranscriptArtifact = transcript
            }, cancellationToken);

        public Task<ProcessingJobSnapshot> CompleteTranscriptOnlyAsync(
            Guid jobId,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var completed = Mutate(jobId, expectedRevision, Required(jobId).State, current => current with
            {
                State = ProcessingState.Completed,
                FailedStage = null,
                ErrorCode = null
            }, cancellationToken);
            OnCompleteTranscriptOnly?.Invoke();
            return completed;
        }

        public Task<ProcessingJobSnapshot> CommitLecturePackageAsync(
            Guid jobId,
            long expectedRevision,
            ArtifactCheckpoint package,
            string sourceTranscriptSha256,
            IReadOnlyList<StudyAssignment> assignments,
            CancellationToken cancellationToken)
        {
            lecturePackages.RemoveAll(item => item.Path == package.Path);
            lecturePackages.Add(package);
            return Mutate(jobId, expectedRevision, ProcessingState.GeneratingStudyPackage, current => current with
            {
                LecturePackageCommitted = true,
                LecturePackageArtifact = package,
                AssignmentsCommitted = true
            }, cancellationToken);
        }

        public Task<IReadOnlyList<ArtifactCheckpoint>> ListLecturePackageArtifactsAsync(
            Guid classId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ArtifactCheckpoint>>(lecturePackages.ToArray());
        }

        public Task<ProcessingJobSnapshot> CompleteGuideAsync(
            Guid jobId,
            long expectedRevision,
            ClassGuideOutcome outcome,
            ArtifactCheckpoint? guide,
            CancellationToken cancellationToken) =>
            Mutate(jobId, expectedRevision, Required(jobId).State, current => current with
            {
                State = ProcessingState.Completed,
                GuideOutcome = outcome
            }, cancellationToken);

        public Task<ProcessingJobSnapshot> MarkNeedsAttentionAsync(
            Guid jobId,
            long expectedRevision,
            ProcessingState failedStage,
            CloudProcessingErrorCode errorCode,
            CancellationToken cancellationToken)
        {
            if (FailMarkNeedsAttention)
            {
                throw new IOException("raw sqlite secret must not escape");
            }

            return Mutate(jobId, expectedRevision, failedStage, current => current with
            {
                State = ProcessingState.NeedsAttention,
                FailedStage = failedStage,
                ErrorCode = errorCode
            }, cancellationToken);
        }

        public Task<ProcessingJobSnapshot> RetryAsync(
            Guid jobId,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var current = Required(jobId);
            return Mutate(jobId, expectedRevision, ProcessingState.NeedsAttention, item => item with
            {
                State = current.FailedStage!.Value,
                FailedStage = null,
                ErrorCode = null
            }, cancellationToken);
        }

        public Task<ProcessingJobSnapshot> CancelAsync(
            Guid jobId,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            Mutate(jobId, expectedRevision, Required(jobId).State, current => current with
            {
                State = ProcessingState.Cancelled
            }, cancellationToken);

        public Task<IReadOnlyList<ProcessingJobSnapshot>> ListResumableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ProcessingJobSnapshot>>(job is null ? [] : [job]);
        }

        internal void RewindToTranscribing(bool clearFinalCheckpoints)
        {
            var current = job ?? throw new InvalidOperationException();
            job = current with
            {
                State = ProcessingState.Transcribing,
                FailedStage = null,
                ErrorCode = null,
                TranscriptCommitted = clearFinalCheckpoints ? false : current.TranscriptCommitted,
                TranscriptArtifact = clearFinalCheckpoints ? null : current.TranscriptArtifact,
                LecturePackageCommitted = clearFinalCheckpoints ? false : current.LecturePackageCommitted,
                LecturePackageArtifact = clearFinalCheckpoints ? null : current.LecturePackageArtifact,
                AssignmentsCommitted = clearFinalCheckpoints ? false : current.AssignmentsCommitted,
                GuideOutcome = ClassGuideOutcome.NotAttempted,
                Revision = current.Revision + 1
            };
        }

        internal void SeedHistoricalLateStage(ProcessingState stage)
        {
            if (stage is not ProcessingState.GeneratingStudyPackage and not ProcessingState.UpdatingClassGuide)
            {
                throw new ArgumentOutOfRangeException(nameof(stage));
            }

            var current = job ?? throw new InvalidOperationException();
            job = current with
            {
                State = stage,
                TranscriptCommitted = true,
                Revision = current.Revision + 1
            };
        }

        internal void SeedLecturePackage(ArtifactCheckpoint package) => lecturePackages.Add(package);

        internal void ReplaceTranscriptArtifact(int index, ArtifactCheckpoint artifact)
        {
            transcriptChunks[index] = transcriptChunks[index] with { Artifact = artifact };
        }

        private ProcessingJobSnapshot Required(Guid jobId) =>
            job is { } current && current.Request.JobId == jobId
                ? current
                : throw new KeyNotFoundException();

        private Task<ProcessingJobSnapshot> Mutate(
            Guid jobId,
            long expectedRevision,
            ProcessingState expectedState,
            Func<ProcessingJobSnapshot, ProcessingJobSnapshot> mutation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Required(jobId);
            if (current.Revision != expectedRevision || current.State != expectedState)
            {
                throw new ProcessingConcurrencyException(jobId);
            }

            job = mutation(current) with { Revision = current.Revision + 1, UpdatedAt = now.AddTicks(current.Revision + 1) };
            return Task.FromResult(job);
        }
    }

    private sealed class FakeArtifactStore : IProcessingArtifactStore
    {
        private readonly Dictionary<string, byte[]> artifacts = new(StringComparer.OrdinalIgnoreCase);
        private int nextArtifact;

        internal Action<string>? OnBeforeWrite { get; set; }
        internal string? BlockWriteContaining { get; set; }
        internal TaskCompletionSource WriteBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseBlockedWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string> CleanupDirectories { get; } = [];

        public Task<bool> VerifyAsync(
            string path,
            string sha256,
            long? expectedByteSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                artifacts.TryGetValue(path, out var bytes) &&
                (expectedByteSize is null || expectedByteSize == bytes.Length) &&
                Hash(bytes) == sha256);
        }

        public Task<ArtifactCheckpoint> WriteJobArtifactAsync(
            ProcessingRequest request,
            string artifactName,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken) =>
            WriteAsync(Path.Combine(request.JobDirectory, artifactName), content, cancellationToken);

        public Task<ArtifactCheckpoint> WriteRecordingArtifactAsync(
            Guid recordingId,
            string artifactName,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken) =>
            WriteAsync(Path.Combine("recordings", recordingId.ToString("D"), artifactName), content, cancellationToken);

        public Task<ArtifactCheckpoint> WriteClassArtifactAsync(
            Guid classId,
            string artifactName,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken) =>
            WriteAsync(Path.Combine("classes", classId.ToString("D"), artifactName), content, cancellationToken);

        public Task<ReadOnlyMemory<byte>?> ReadVerifiedAsync(
            ArtifactCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte>? result = artifacts.TryGetValue(checkpoint.Path, out var bytes) && Hash(bytes) == checkpoint.Sha256
                ? bytes
                : null;
            return Task.FromResult(result);
        }

        public Task CleanupJobAsync(
            ProcessingRequest request,
            IReadOnlyCollection<string> publishedPaths,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanupDirectories.Add(request.JobDirectory);
            return Task.CompletedTask;
        }

        internal void Register(AudioChunk chunk, byte[] bytes) => artifacts[chunk.Path] = bytes;

        internal void Corrupt(string path) => artifacts[path] = [0xff];

        internal ArtifactCheckpoint Replace<T>(string path, T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            artifacts[path] = bytes;
            return new ArtifactCheckpoint(path, Hash(bytes));
        }

        internal ArtifactCheckpoint StoreRecordingPackage(Guid recordingId, StudyPackage package) =>
            WriteAsync(
                Path.Combine("recordings", recordingId.ToString("D"), "previous-package.json"),
                JsonSerializer.SerializeToUtf8Bytes(package),
                CancellationToken.None).GetAwaiter().GetResult();

        private async Task<ArtifactCheckpoint> WriteAsync(
            string suggestedPath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnBeforeWrite?.Invoke(suggestedPath);
            if (BlockWriteContaining is { } blocked && suggestedPath.Contains(blocked, StringComparison.Ordinal))
            {
                WriteBlocked.TrySetResult();
                await ReleaseBlockedWrite.Task.WaitAsync(cancellationToken);
            }

            var path = $"{suggestedPath}.{nextArtifact++}";
            var bytes = content.ToArray();
            artifacts[path] = bytes;
            return new ArtifactCheckpoint(path, Hash(bytes));
        }

        private static string Hash(ReadOnlySpan<byte> bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class FakeAudioChunkPreparer(FakeArtifactStore artifacts, string jobDirectory) : IAudioChunkPreparer
    {
        internal int Calls { get; private set; }
        internal Action? OnCall { get; set; }
        internal int? ChangedIndex { get; set; }
        internal string? ReturnedDirectory { get; set; }

        public Task<IReadOnlyList<AudioChunk>> PrepareAsync(
            string mp4Path,
            string requestedJobDirectory,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            OnCall?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(jobDirectory, requestedJobDirectory);
            var chunks = new[]
            {
                Chunk(0, 0, 10_000, 11),
                Chunk(1, 5_000, 15_000, 12)
            };
            foreach (var chunk in chunks)
            {
                var value = ChangedIndex == chunk.Index ? (byte)9 : (byte)(chunk.Index + 1);
                artifacts.Register(chunk, Enumerable.Repeat(value, checked((int)chunk.ByteSize)).ToArray());
            }

            return Task.FromResult<IReadOnlyList<AudioChunk>>(chunks);
        }

        private AudioChunk Chunk(int index, long start, long end, long size)
        {
            var value = ChangedIndex == index ? (byte)9 : (byte)(index + 1);
            var bytes = Enumerable.Repeat(value, checked((int)size)).ToArray();
            return new AudioChunk(
                index,
                Path.Combine(ReturnedDirectory ?? jobDirectory, $"chunk-{index}.m4a"),
                start,
                end,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                size);
        }
    }

    private sealed class FakeTranscriptionClient : ITranscriptionClient
    {
        internal List<int> RequestedChunkIndexes { get; } = [];
        internal int? FailIndex { get; set; }
        internal Action? OnCall { get; set; }

        public Task<TranscriptChunk> TranscribeAsync(
            AudioChunk chunk,
            IProgress<TranscriptionActivity>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnCall?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            RequestedChunkIndexes.Add(chunk.Index);
            progress?.Report(new TranscriptionActivity(
                TranscriptionActivityKind.Transcribing,
                CompletedBytes: 12,
                TotalBytes: 24));
            if (FailIndex == chunk.Index)
            {
                throw new InvalidOperationException("raw provider secret must not escape");
            }

            var start = chunk.Index == 0 ? 0 : 10_001;
            return Task.FromResult(new TranscriptChunk(
                chunk.Index,
                chunk.StartMilliseconds,
                chunk.EndMilliseconds,
                [new TranscriptSegment(start, chunk.EndMilliseconds, $"chunk {chunk.Index}")]));
        }

        internal void ResetRequests() => RequestedChunkIndexes.Clear();
    }

    private sealed class FakeVideoRecycler : IVideoRecycler
    {
        internal int Calls { get; private set; }

        public Task<RecycleResult> RecycleAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new RecycleResult(Recycled: true, RecycledPath: path));
        }
    }

    private sealed class FakeVideoDeletionStore : IVideoDeletionStore
    {
        public Task MarkVideoUnavailableAsync(Guid recordingId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeStudyGenerationClient : IStudyGenerationClient
    {
        internal int LectureCalls { get; private set; }
        internal int GuideCalls { get; private set; }
        internal bool FailLecture { get; set; }
        internal bool FailGuide { get; set; }
        internal Action? OnLecture { get; set; }
        internal Action? OnGuide { get; set; }

        public Task<StudyPackage> GenerateLectureAsync(Transcript transcript, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnLecture?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            LectureCalls++;
            if (FailLecture)
            {
                throw new InvalidOperationException("raw lecture provider body");
            }

            return Task.FromResult(Package());
        }

        public Task<ClassStudyGuide> GenerateGuideAsync(
            IReadOnlyList<StudyPackage> lectures,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnGuide?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            GuideCalls++;
            if (FailGuide)
            {
                throw new InvalidOperationException("raw guide provider body");
            }

            return Task.FromResult(new ClassStudyGuide(1, Package().StudyGuideContributions));
        }

        internal static StudyPackage Package() => new(
            1,
            "Lecture",
            new DateOnly(2026, 8, 19),
            "Summary",
            [new NoteSection("Topic", "Body", [new TimestampReference(0, 1)])],
            [new KeyTerm("Term", "Definition", [new TimestampReference(0, 1)])],
            [new StudyAssignment("Read", "Friday", null, 0.5, new TimestampReference(0, 1))],
            [new ReviewQuestion("Question", "Answer", "Topic")],
            [new StudyGuideContribution("Topic", ["Contribution"])]);
    }
}
