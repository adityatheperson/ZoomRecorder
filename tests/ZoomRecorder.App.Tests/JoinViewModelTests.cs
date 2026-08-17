using ZoomRecorder.App.ViewModels;
using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.App.Tests;

public sealed class JoinViewModelTests
{
    [Fact]
    public async Task Join_failure_stays_on_join_page_and_surfaces_message()
    {
        var navigator = new RecordingNavigator();
        var viewModel = new JoinViewModel(new FailingJoinFlow("Microphone unavailable"), navigator)
        {
            MeetingInput = "1234567890",
            DisplayName = "Aditya"
        };

        await viewModel.JoinAndRecordAsync();

        Assert.Equal("Microphone unavailable", viewModel.ErrorMessage);
        Assert.Equal(0, navigator.MeetingNavigationCount);
        Assert.False(viewModel.IsJoining);
    }

    [Fact]
    public async Task Successful_join_navigates_to_meeting_once()
    {
        var navigator = new RecordingNavigator();
        var flow = new SuccessfulJoinFlow();
        var viewModel = new JoinViewModel(flow, navigator)
        {
            MeetingInput = "https://zoom.us/j/1234567890?pwd=abc",
            DisplayName = "Aditya"
        };

        await viewModel.JoinAndRecordAsync();

        Assert.Equal(1, navigator.MeetingNavigationCount);
        Assert.Equal("1234567890", flow.Request?.MeetingId);
        Assert.Equal("abc", flow.Request?.Passcode);
        Assert.Null(viewModel.ErrorMessage);
    }

    private sealed class FailingJoinFlow(string message) : IJoinFlow
    {
        public Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(message));
    }

    private sealed class SuccessfulJoinFlow : IJoinFlow
    {
        public MeetingJoinRequest? Request { get; private set; }

        public Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNavigator : IAppNavigator
    {
        public int MeetingNavigationCount { get; private set; }

        public void ShowMeeting() => MeetingNavigationCount++;
    }
}
