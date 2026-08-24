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

    [Theory]
    [InlineData(ProcessingState.PreparingAudio, "Preparing audio")]
    [InlineData(ProcessingState.Transcribing, "Transcribing")]
    [InlineData(ProcessingState.GeneratingStudyPackage, "Creating study materials")]
    [InlineData(ProcessingState.UpdatingClassGuide, "Updating class guide")]
    [InlineData(ProcessingState.Completed, "Completed")]
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
        Assert.Equal("1.2 KB", vm.EstimatedUploadText);
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
