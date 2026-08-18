using System.Text.Json;
using System.Text.Json.Nodes;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.Core.Tests.Processing;

public sealed class ProcessingDomainTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Job_moves_forward_through_active_stages()
    {
        var job = NewJob();

        job.TransitionTo(ProcessingState.PreparingAudio, StartedAt.AddMinutes(1));
        job.TransitionTo(ProcessingState.Transcribing, StartedAt.AddMinutes(2));
        job.TransitionTo(ProcessingState.GeneratingStudyPackage, StartedAt.AddMinutes(3));
        job.TransitionTo(ProcessingState.UpdatingClassGuide, StartedAt.AddMinutes(4));

        Assert.Equal(ProcessingState.UpdatingClassGuide, job.State);
        Assert.Equal(StartedAt.AddMinutes(4), job.UpdatedAt);
    }

    [Fact]
    public void Job_rejects_skipped_or_backward_stage_transitions()
    {
        var job = NewJob();

        Assert.Throws<InvalidProcessingTransitionException>(() =>
            job.TransitionTo(ProcessingState.Transcribing, StartedAt.AddMinutes(1)));

        job.TransitionTo(ProcessingState.PreparingAudio, StartedAt.AddMinutes(1));
        Assert.Throws<InvalidProcessingTransitionException>(() =>
            job.TransitionTo(ProcessingState.ReadyToProcess, StartedAt.AddMinutes(2)));
    }

    [Fact]
    public void Completion_requires_every_commit_and_class_guide_outcome()
    {
        var job = JobAtUpdatingGuide();
        var completionTime = StartedAt.AddMinutes(10);

        job.MarkTranscriptCommitted(StartedAt.AddMinutes(5));
        job.MarkLecturePackageCommitted(StartedAt.AddMinutes(6));
        job.MarkAssignmentsCommitted(StartedAt.AddMinutes(7));

        Assert.Throws<InvalidProcessingTransitionException>(() =>
            job.TransitionTo(ProcessingState.Completed, completionTime));

        job.RecordClassGuideOutcome(StartedAt.AddMinutes(8));
        job.TransitionTo(ProcessingState.Completed, completionTime);

        Assert.Equal(ProcessingState.Completed, job.State);
        Assert.Equal(completionTime, job.CompletedAt);
    }

    [Fact]
    public void Needs_attention_records_a_closed_error_identifier_and_retries_failed_stage()
    {
        var job = NewJob();
        job.TransitionTo(ProcessingState.PreparingAudio, StartedAt.AddMinutes(1));
        job.TransitionTo(ProcessingState.Transcribing, StartedAt.AddMinutes(2));

        job.MarkNeedsAttention(CloudProcessingErrorCode.TranscriptionUnavailable, StartedAt.AddMinutes(3));

        Assert.Equal(ProcessingState.NeedsAttention, job.State);
        Assert.Equal(ProcessingState.Transcribing, job.FailedStage);
        Assert.Equal(CloudProcessingErrorCode.TranscriptionUnavailable, job.ErrorCode);

        job.Retry(StartedAt.AddMinutes(4));

        Assert.Equal(ProcessingState.Transcribing, job.State);
        Assert.Null(job.FailedStage);
        Assert.Null(job.ErrorCode);
    }

    [Fact]
    public void Processing_job_exposes_no_free_text_error_input_path()
    {
        var freeTextParameters = typeof(ProcessingJob)
            .GetMethods()
            .Where(method => method.Name == nameof(ProcessingJob.MarkNeedsAttention))
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(string));

        Assert.Empty(freeTextParameters);
    }

    [Fact]
    public void Invalid_error_identifier_leaves_job_unchanged()
    {
        var job = NewJob();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            job.MarkNeedsAttention((CloudProcessingErrorCode)int.MaxValue, StartedAt.AddMinutes(1)));

        Assert.Equal(ProcessingState.ReadyToProcess, job.State);
        Assert.Null(job.FailedStage);
        Assert.Null(job.ErrorCode);
        Assert.Equal(StartedAt, job.UpdatedAt);
    }

    [Theory]
    [InlineData(ProcessingState.ReadyToProcess)]
    [InlineData(ProcessingState.PreparingAudio)]
    [InlineData(ProcessingState.Transcribing)]
    [InlineData(ProcessingState.GeneratingStudyPackage)]
    [InlineData(ProcessingState.UpdatingClassGuide)]
    public void Active_jobs_can_be_cancelled(ProcessingState target)
    {
        var job = NewJob();
        MoveTo(job, target);

        job.Cancel(StartedAt.AddHours(1));

        Assert.Equal(ProcessingState.Cancelled, job.State);
    }

    [Fact]
    public void Needs_attention_job_can_be_cancelled_but_terminal_job_cannot()
    {
        var attentionJob = NewJob();
        attentionJob.MarkNeedsAttention(CloudProcessingErrorCode.AudioPreparationFailed, StartedAt.AddMinutes(1));
        attentionJob.Cancel(StartedAt.AddMinutes(2));
        Assert.Equal(ProcessingState.Cancelled, attentionJob.State);

        Assert.Throws<InvalidProcessingTransitionException>(() =>
            attentionJob.Cancel(StartedAt.AddMinutes(3)));
    }

    [Fact]
    public void Start_preserves_job_identity_options_and_timestamp()
    {
        var jobId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();

        var job = ProcessingJob.Start(jobId, recordingId, deleteVideo: true, StartedAt);

        Assert.Equal(jobId, job.Id);
        Assert.Equal(recordingId, job.RecordingId);
        Assert.True(job.DeleteVideo);
        Assert.Equal(ProcessingState.ReadyToProcess, job.State);
        Assert.Equal(StartedAt, job.StartedAt);
        Assert.Equal(StartedAt, job.UpdatedAt);
    }

    [Fact]
    public void Study_schema_accepts_low_confidence_and_round_trips_required_collections()
    {
        var package = ValidPackage(confidence: 0);

        StudyPackageValidator.Validate(package);
        var json = JsonSerializer.Serialize(package);
        var roundTripped = JsonSerializer.Deserialize<StudyPackage>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped.SchemaVersion);
        Assert.Single(roundTripped.Assignments);
        Assert.Equal(0, roundTripped.Assignments[0].Confidence);
        Assert.Single(roundTripped.NoteSections);
        Assert.Single(roundTripped.KeyTerms);
        Assert.Single(roundTripped.ReviewQuestions);
        Assert.Single(roundTripped.StudyGuideContributions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Study_schema_rejects_unsupported_versions(int version)
    {
        Assert.Throws<StudyPackageValidationException>(() =>
            StudyPackageValidator.Validate(ValidPackage() with { SchemaVersion = version }));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Study_schema_rejects_assignment_confidence_outside_unit_interval(double confidence)
    {
        Assert.Throws<StudyPackageValidationException>(() =>
            StudyPackageValidator.Validate(ValidPackage(confidence)));
    }

    [Fact]
    public void Study_schema_rejects_blank_required_text_and_assignment_description()
    {
        Assert.Throws<StudyPackageValidationException>(() =>
            StudyPackageValidator.Validate(ValidPackage() with { LectureTitle = " " }));

        var package = ValidPackage() with
        {
            Assignments = [ValidPackage().Assignments[0] with { Description = "" }]
        };
        Assert.Throws<StudyPackageValidationException>(() => StudyPackageValidator.Validate(package));
    }

    [Fact]
    public void Study_schema_rejects_missing_required_collections()
    {
        Assert.Throws<StudyPackageValidationException>(() =>
            StudyPackageValidator.Validate(ValidPackage() with { NoteSections = null! }));
        Assert.Throws<StudyPackageValidationException>(() =>
            StudyPackageValidator.Validate(ValidPackage() with { Assignments = null! }));
    }

    [Fact]
    public void Study_schema_rejects_a_lecture_date_omitted_during_deserialization()
    {
        var json = JsonSerializer.SerializeToNode(ValidPackage())!.AsObject();
        Assert.True(json.Remove(nameof(StudyPackage.LectureDate)));
        var package = json.Deserialize<StudyPackage>();

        Assert.NotNull(package);
        Assert.Equal(default, package.LectureDate);
        Assert.Throws<StudyPackageValidationException>(() => StudyPackageValidator.Validate(package));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(20, 10)]
    public void Study_schema_rejects_invalid_timestamp_ranges(long start, long end)
    {
        var package = ValidPackage() with
        {
            NoteSections =
            [
                new NoteSection("Topic", "Explanation", [new TimestampReference(start, end)])
            ]
        };

        Assert.Throws<StudyPackageValidationException>(() => StudyPackageValidator.Validate(package));
    }

    [Fact]
    public async Task Processing_ports_preserve_cancellation_tokens_in_their_contracts()
    {
        IAudioChunkPreparer preparer = new CancellingAudioChunkPreparer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            preparer.PrepareAsync("lecture.mp4", "job", 1024, cancellation.Token));

        var methods = new[]
        {
            typeof(ITranscriptionClient).GetMethod(nameof(ITranscriptionClient.TranscribeAsync)),
            typeof(IStudyGenerationClient).GetMethod(nameof(IStudyGenerationClient.GenerateLectureAsync)),
            typeof(IStudyGenerationClient).GetMethod(nameof(IStudyGenerationClient.GenerateGuideAsync)),
            typeof(ICredentialVault).GetMethod(nameof(ICredentialVault.GetApiKeyAsync)),
            typeof(ICredentialVault).GetMethod(nameof(ICredentialVault.SaveApiKeyAsync)),
            typeof(ICredentialVault).GetMethod(nameof(ICredentialVault.DeleteApiKeyAsync)),
            typeof(IVideoRecycler).GetMethod(nameof(IVideoRecycler.RecycleAsync))
        };

        Assert.All(methods, method => Assert.Equal(typeof(CancellationToken), method!.GetParameters()[^1].ParameterType));
    }

    private static ProcessingJob NewJob() =>
        ProcessingJob.Start(Guid.NewGuid(), Guid.NewGuid(), deleteVideo: false, StartedAt);

    private static ProcessingJob JobAtUpdatingGuide()
    {
        var job = NewJob();
        MoveTo(job, ProcessingState.UpdatingClassGuide);
        return job;
    }

    private static void MoveTo(ProcessingJob job, ProcessingState target)
    {
        var path = new[]
        {
            ProcessingState.PreparingAudio,
            ProcessingState.Transcribing,
            ProcessingState.GeneratingStudyPackage,
            ProcessingState.UpdatingClassGuide
        };

        var minute = 1;
        foreach (var state in path)
        {
            if (target == ProcessingState.ReadyToProcess)
            {
                return;
            }

            job.TransitionTo(state, StartedAt.AddMinutes(minute++));
            if (state == target)
            {
                return;
            }
        }
    }

    private static StudyPackage ValidPackage(double confidence = 0.5) =>
        new(
            SchemaVersion: 1,
            LectureTitle: "Thermodynamics",
            LectureDate: new DateOnly(2026, 8, 18),
            ShortSummary: "Energy and entropy.",
            NoteSections:
            [
                new NoteSection("Entropy", "Entropy measures multiplicity.", [new TimestampReference(1_000, 4_000)])
            ],
            KeyTerms:
            [
                new KeyTerm("Entropy", "A measure of multiplicity.", [new TimestampReference(1_500, 2_500)])
            ],
            Assignments:
            [
                new StudyAssignment(
                    "Read chapter 3", "Friday", new DateOnly(2026, 8, 21), confidence,
                    new TimestampReference(5_000, 6_000))
            ],
            ReviewQuestions:
            [
                new ReviewQuestion("What is entropy?", "A measure of multiplicity.", "Entropy")
            ],
            StudyGuideContributions:
            [
                new StudyGuideContribution("Thermodynamics", ["Define entropy", "Review the second law"])
            ]);

    private sealed class CancellingAudioChunkPreparer : IAudioChunkPreparer
    {
        public Task<IReadOnlyList<AudioChunk>> PrepareAsync(
            string mp4Path,
            string jobDirectory,
            long maxBytes,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<IReadOnlyList<AudioChunk>>(cancellationToken);
    }
}
