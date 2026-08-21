using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.ViewModels.Library;

public sealed class ProcessingViewModelTests
{
    [Fact]
    public async Task Start_requires_cloud_notice_and_never_defaults_delete_video_on()
    {
        var notice = new NoticePresenter(true);
        var started = false;
        var vm = new ProcessingViewModel(
            "Biology 101", 2_000_000, savedDeleteDefault: false, notice,
            (_, _) => { started = true; return Task.CompletedTask; },
            _ => Task.CompletedTask);

        Assert.False(vm.DeleteVideoAfterSuccess);
        await vm.StartAsync(default);

        Assert.True(vm.CloudNoticeWasPresented);
        Assert.True(started);
        Assert.Contains("audio", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Biology 101", notice.Message);
    }

    [Fact]
    public async Task Declined_cloud_notice_does_not_start_processing()
    {
        var notice = new NoticePresenter(false);
        var starts = 0;
        var vm = new ProcessingViewModel(
            "Chemistry", null, false, notice,
            (_, _) => { starts++; return Task.CompletedTask; },
            _ => Task.CompletedTask);

        await vm.StartAsync(default);

        Assert.Equal(0, starts);
        Assert.False(vm.IsProcessing);
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

    private sealed class NoticePresenter(bool accepted) : ICloudNoticePresenter
    {
        public string Message { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
        {
            Message = message;
            return Task.FromResult(accepted);
        }
    }
}
