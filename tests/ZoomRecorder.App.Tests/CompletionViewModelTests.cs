using ZoomRecorder.App.ViewModels;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.Tests;

public sealed class CompletionViewModelTests
{
    [Fact]
    public void Completion_exposes_finalized_recording_metadata()
    {
        var result = new RecordingResult("C:\\Videos\\meeting.mp4", TimeSpan.FromMinutes(42), 52_428_800);

        var viewModel = new CompletionViewModel(result);

        Assert.Equal("meeting.mp4", viewModel.FileName);
        Assert.Equal("42:00", viewModel.DurationText);
        Assert.Equal("50.0 MB", viewModel.FileSizeText);
        Assert.Equal(result.Path, viewModel.Path);
    }
}
