using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.ViewModels.Library;

public sealed class LectureDetailViewModelTests
{
    [Fact]
    public void Credential_error_offers_settings_action()
    {
        var vm = Create(videoAvailable: true);

        vm.ApplyFailure(CloudProcessingErrorCode.CredentialUnavailable);

        Assert.Equal("Check API key", vm.RecoveryActionText);
        Assert.True(vm.CanOpenSettings);
    }

    [Fact]
    public async Task Transcript_save_marks_materials_stale()
    {
        var saved = string.Empty;
        var vm = Create(videoAvailable: true, saveTranscript: (text, _) =>
        {
            saved = text;
            return Task.CompletedTask;
        });
        vm.TranscriptText = "Corrected mitosis explanation";

        await vm.SaveTranscriptAsync(default);

        Assert.Equal("Corrected mitosis explanation", saved);
        Assert.True(vm.StudyMaterialsAreStale);
        Assert.True(vm.CanRefreshStudyMaterials);
    }

    [Fact]
    public async Task Refresh_requires_cloud_confirmation()
    {
        var refreshes = 0;
        var notice = new Notice(false);
        var vm = Create(videoAvailable: true, notice: notice, refresh: _ =>
        {
            refreshes++;
            return Task.CompletedTask;
        });
        vm.TranscriptText = "changed";
        await vm.SaveTranscriptAsync(default);

        await vm.RefreshStudyMaterialsAsync(default);

        Assert.Equal(0, refreshes);
        Assert.Contains("cloud", notice.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timestamp_seeking_is_disabled_after_video_deletion()
    {
        var vm = Create(videoAvailable: false);

        Assert.False(vm.CanSeekVideo);
        Assert.Contains("deleted", vm.SeekUnavailableText!, StringComparison.OrdinalIgnoreCase);
    }

    private static LectureDetailViewModel Create(
        bool videoAvailable,
        Func<string, CancellationToken, Task>? saveTranscript = null,
        Func<CancellationToken, Task>? refresh = null,
        ICloudNoticePresenter? notice = null) =>
        new(
            new RecordingRecord(Guid.NewGuid(), Guid.NewGuid(), "C:\\lecture.mp4", "lecture.mp4", null,
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(50), 100, videoAvailable),
            saveTranscript ?? ((_, _) => Task.CompletedTask),
            refresh ?? (_ => Task.CompletedTask),
            notice ?? new Notice(true));

    private sealed class Notice(bool accepted) : ICloudNoticePresenter
    {
        public string Message { get; private set; } = string.Empty;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
        {
            Message = message;
            return Task.FromResult(accepted);
        }
    }
}
