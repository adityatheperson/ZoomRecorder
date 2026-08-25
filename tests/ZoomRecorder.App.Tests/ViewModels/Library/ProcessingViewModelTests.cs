using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.ViewModels.Library;

public sealed class ProcessingViewModelTests
{
    [Fact]
    public async Task Start_uses_inline_disclosure_without_opening_a_second_modal_or_enabling_video_deletion()
    {
        var notice = new NoticePresenter(true);
        var starts = 0;
        var vm = new ProcessingViewModel(
            "Biology 101", 2_000_000, savedDeleteDefault: false, notice,
            (_, _) => { starts++; return Task.CompletedTask; },
            _ => Task.CompletedTask);

        Assert.False(vm.DeleteVideoAfterSuccess);
        await vm.StartAsync(default);

        Assert.Equal(0, notice.Calls);
        Assert.Equal(1, starts);
        Assert.Equal("Transcribe locally", vm.PrimaryActionText);
        Assert.False(vm.ShowsCloudControls);
        Assert.False(vm.SupportsVideoDeletion);
        Assert.Null(vm.EstimatedUploadText);
        Assert.Null(vm.EstimatedCostText);
    }

    [Fact]
    public async Task Processing_failure_keeps_the_dialog_alive_and_shows_the_error()
    {
        var vm = new ProcessingViewModel(
            "Biology 101", null, savedDeleteDefault: false, new NoticePresenter(true),
            (_, _) => throw new ProcessingOperationException(CloudProcessingErrorCode.StudyGenerationUnavailable),
            _ => Task.CompletedTask);

        await vm.StartAsync(default);

        Assert.False(vm.IsProcessing);
        Assert.False(vm.IsProgressIndeterminate);
        Assert.True(vm.HasError);
        Assert.Equal("Study materials could not be generated. Try again.", vm.StatusText);
    }

    [Fact]
    public async Task Queued_needs_attention_progress_does_not_overwrite_the_actionable_error()
    {
        var vm = new ProcessingViewModel(
            "Biology 101", null, false, new NoticePresenter(true),
            (_, _) => throw new ProcessingOperationException(CloudProcessingErrorCode.TranscriptionUnavailable),
            _ => Task.CompletedTask);

        await vm.StartAsync(default);
        vm.Apply(new ProcessingProgress(
            Guid.NewGuid(), ProcessingState.NeedsAttention, 0, 1,
            ClassGuideOutcome.NotAttempted, CloudProcessingErrorCode.TranscriptionUnavailable));

        Assert.Equal("Transcription could not be completed. Try again.", vm.StatusText);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task Unexpected_start_failure_is_contained_and_shows_a_safe_error()
    {
        var vm = new ProcessingViewModel(
            "Biology 101", null, false, new NoticePresenter(true),
            (_, _) => throw new InvalidOperationException("private diagnostic"),
            _ => Task.CompletedTask);

        await vm.StartAsync(default);

        Assert.False(vm.IsProcessing);
        Assert.False(vm.IsProgressIndeterminate);
        Assert.True(vm.HasError);
        Assert.Equal("Processing stopped unexpectedly. Try again.", vm.StatusText);
    }

    [Fact]
    public async Task Cancellation_is_contained_at_the_dialog_boundary()
    {
        var vm = new ProcessingViewModel(
            "Biology 101", null, false, new NoticePresenter(true),
            (_, _) => throw new OperationCanceledException(),
            _ => Task.CompletedTask);

        await vm.StartAsync(default);

        Assert.False(vm.IsProcessing);
        Assert.True(vm.HasError);
        Assert.Equal("Processing was cancelled.", vm.StatusText);
    }

    [Fact]
    public async Task Cancel_signals_the_active_operation_and_waits_for_its_cleanup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = false;
        var cancelCalls = 0;
        var vm = new ProcessingViewModel(
            "Biology 101", null, false, new NoticePresenter(true),
            async (_, token) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally
                {
                    await cleanupMayFinish.Task;
                    cleanupFinished = true;
                }
            },
            _ => { cancelCalls++; return Task.CompletedTask; });

        var processing = vm.StartAsync(default);
        await started.Task;
        var cancelling = vm.CancelAsync(default);

        Assert.False(cancelling.IsCompleted);
        Assert.Equal(1, cancelCalls);
        cleanupMayFinish.SetResult();
        await Task.WhenAll(processing, cancelling);
        Assert.True(cleanupFinished);
        Assert.False(vm.IsProcessing);
    }

    [Fact]
    public async Task Retry_clears_the_previous_error_before_starting_again()
    {
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var vm = new ProcessingViewModel(
            "Biology 101", null, false, new NoticePresenter(true),
            async (_, _) =>
            {
                if (++attempts == 1)
                    throw new ProcessingOperationException(CloudProcessingErrorCode.StudyGenerationUnavailable);
                retryStarted.SetResult();
                await releaseRetry.Task;
            },
            _ => Task.CompletedTask);

        await vm.StartAsync(default);
        var retry = vm.StartAsync(default);
        await retryStarted.Task;

        Assert.False(vm.HasError);
        Assert.Equal("Starting processing", vm.StatusText);

        releaseRetry.SetResult();
        await retry;
    }

    [Fact]
    public async Task Retry_resumes_the_failed_job_instead_of_creating_it_again()
    {
        var starts = 0;
        var resumes = 0;
        var vm = new ProcessingViewModel(
            "Biology 101", null, false, new NoticePresenter(true),
            (_, _) =>
            {
                starts++;
                throw new ProcessingOperationException(CloudProcessingErrorCode.TranscriptionUnavailable);
            },
            _ => Task.CompletedTask,
            resume: _ => { resumes++; return Task.CompletedTask; });

        await vm.StartAsync(default);
        await vm.StartAsync(default);

        Assert.Equal(1, starts);
        Assert.Equal(1, resumes);
    }

    [Theory]
    [InlineData(ProcessingState.PreparingAudio, "Preparing audio")]
    [InlineData(ProcessingState.Transcribing, "Transcribing locally")]
    [InlineData(ProcessingState.GeneratingStudyPackage, "Transcribing locally")]
    [InlineData(ProcessingState.UpdatingClassGuide, "Transcribing locally")]
    [InlineData(ProcessingState.Completed, "Transcript ready")]
    [InlineData(ProcessingState.NeedsAttention, "Needs attention")]
    [InlineData(ProcessingState.Cancelled, "Cancelled")]
    public void Progress_uses_stable_user_facing_labels(ProcessingState state, string expected)
    {
        var vm = new ProcessingViewModel("Physics", null, false, new NoticePresenter(true), (_, _) => Task.CompletedTask, _ => Task.CompletedTask);

        vm.Apply(new ProcessingProgress(Guid.NewGuid(), state, 1, 2, ClassGuideOutcome.NotAttempted, null));

        Assert.Equal(expected, vm.StatusText);
    }

    [Fact]
    public void Estimated_cost_is_hidden_when_pricing_is_not_configured()
    {
        var vm = new ProcessingViewModel("Physics", 1234, false, new NoticePresenter(true), (_, _) => Task.CompletedTask, _ => Task.CompletedTask);

        Assert.Null(vm.EstimatedCostText);
        Assert.Null(vm.EstimatedUploadText);
    }

    [Fact]
    public void Model_download_is_determinate_but_inference_and_cpu_fallback_are_indeterminate()
    {
        var vm = new ProcessingViewModel("Physics", null, false, new NoticePresenter(true), (_, _) => Task.CompletedTask, _ => Task.CompletedTask);
        var jobId = Guid.NewGuid();

        vm.Apply(new ProcessingProgress(jobId, ProcessingState.Transcribing, 0, 1, ClassGuideOutcome.NotAttempted, null,
            new TranscriptionActivity(TranscriptionActivityKind.AcquiringModel, 25, 100), 25, 100));
        Assert.Equal("Downloading English transcription model (~500 MB)", vm.StatusText);
        Assert.False(vm.IsProgressIndeterminate);
        Assert.Equal(25, vm.ProgressValue);
        Assert.Equal(100, vm.ProgressMaximum);

        vm.Apply(new ProcessingProgress(jobId, ProcessingState.Transcribing, 0, 1, ClassGuideOutcome.NotAttempted, null,
            new TranscriptionActivity(TranscriptionActivityKind.Transcribing)));
        Assert.Equal("Transcribing locally", vm.StatusText);
        Assert.True(vm.IsProgressIndeterminate);

        vm.Apply(new ProcessingProgress(jobId, ProcessingState.Transcribing, 0, 1, ClassGuideOutcome.NotAttempted, null,
            new TranscriptionActivity(TranscriptionActivityKind.UsingCpuFallback)));
        Assert.Equal("Using CPU fallback", vm.StatusText);
        Assert.True(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task Recycle_unavailable_requires_separate_confirmation_before_permanent_delete()
    {
        var notice = new NoticePresenter(false);
        var deletes = 0;
        var vm = new ProcessingViewModel(
            "Physics", null, false, notice, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            permanentDelete: _ => { deletes++; return Task.CompletedTask; });

        vm.ApplyRecycleUnavailable();
        await vm.ConfirmPermanentDeleteAsync(default);

        Assert.True(vm.PermanentDeleteDecisionPending);
        Assert.Equal(0, deletes);
    }

    private sealed class NoticePresenter(bool accepted) : ICloudNoticePresenter
    {
        public string Message { get; private set; } = string.Empty;
        public int Calls { get; private set; }

        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
        {
            Calls++;
            Message = message;
            return Task.FromResult(accepted);
        }
    }
}
