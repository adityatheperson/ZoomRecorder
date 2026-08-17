using ZoomRecorder.App.ViewModels;
using ZoomRecorder.Core.Orchestration;

namespace ZoomRecorder.App.Tests;

public sealed class MeetingViewModelTests
{
    [Fact]
    public void Required_failure_sets_error_visual_state()
    {
        var statuses = new TestStatusSource();
        using var viewModel = new MeetingViewModel(statuses);

        statuses.Publish(MeetingStatus.Failed("Meeting audio stopped"));

        Assert.True(viewModel.HasRecordingError);
        Assert.False(viewModel.IsRecordingHealthy);
        Assert.Equal("Meeting audio stopped", viewModel.StatusText);
    }

    private sealed class TestStatusSource : IObservable<MeetingStatus>
    {
        private IObserver<MeetingStatus>? _observer;

        public IDisposable Subscribe(IObserver<MeetingStatus> observer)
        {
            _observer = observer;
            return new Subscription(() => _observer = null);
        }

        public void Publish(MeetingStatus status) => _observer?.OnNext(status);

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
