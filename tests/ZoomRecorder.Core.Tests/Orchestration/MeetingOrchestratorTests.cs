using ZoomRecorder.Core.Lifecycle;
using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Orchestration;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.Core.Tests.Orchestration;

public sealed class MeetingOrchestratorTests
{
    private static readonly MeetingJoinRequest Request = new("1234567890", null, "Aditya");

    [Fact]
    public async Task Join_starts_recording_before_entering_meeting()
    {
        var calls = new List<string>();
        var meeting = new RecordingMeetingClient(calls);
        var recording = new RecordingSession(calls);
        var orchestrator = new MeetingOrchestrator(meeting, recording, new RecordingStore(calls), new MeetingLifecycle());

        await orchestrator.JoinAndRecordAsync(Request, default);

        Assert.Equal(new[] { "store", "prepare", "record", "enter" }, calls);
        Assert.Equal(AppState.InMeetingRecording, orchestrator.State);
    }

    [Fact]
    public async Task Recording_failure_prevents_meeting_entry_and_cancels_preparation()
    {
        var calls = new List<string>();
        var meeting = new RecordingMeetingClient(calls);
        var recording = new RecordingSession(calls, startException: new RecordingStartException("Low disk space"));
        var orchestrator = new MeetingOrchestrator(meeting, recording, new RecordingStore(calls), new MeetingLifecycle());

        var exception = await Assert.ThrowsAsync<RecordingStartException>(() => orchestrator.JoinAndRecordAsync(Request, default));

        Assert.Equal("Low disk space", exception.Message);
        Assert.Equal(new[] { "store", "prepare", "record", "finalize-if-started", "cancel" }, calls);
        Assert.DoesNotContain("enter", calls);
        Assert.Equal(AppState.RecoverableError, orchestrator.State);
    }

    [Fact]
    public async Task Cleanup_failure_does_not_replace_the_meeting_entry_failure()
    {
        var calls = new List<string>();
        var meetingError = new InvalidOperationException("The host removed you from the meeting.");
        var meeting = new RecordingMeetingClient(calls, meetingError);
        var recording = new RecordingSession(calls, finalizeException: new InvalidOperationException("Encoder was never opened"));
        var orchestrator = new MeetingOrchestrator(meeting, recording, new RecordingStore(calls), new MeetingLifecycle());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.JoinAndRecordAsync(Request, default));

        Assert.Same(meetingError, exception);
        Assert.Equal(AppState.RecoverableError, orchestrator.State);
        Assert.Equal(new[] { "store", "prepare", "record", "enter", "finalize-if-started", "cancel" }, calls);
    }

    private sealed class RecordingMeetingClient(List<string> calls, Exception? enterException = null) : IMeetingClient
    {
        public Task PrepareAsync(MeetingJoinRequest request, CancellationToken cancellationToken)
        {
            calls.Add("prepare");
            return Task.CompletedTask;
        }

        public Task EnterAsync(CancellationToken cancellationToken)
        {
            calls.Add("enter");
            return enterException is null ? Task.CompletedTask : Task.FromException(enterException);
        }

        public Task CancelPreparedMeetingAsync(CancellationToken cancellationToken)
        {
            calls.Add("cancel");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSession(List<string> calls, Exception? startException = null, Exception? finalizeException = null) : IRecordingSession
    {
        public Task StartAsync(RecordingTarget target, CancellationToken cancellationToken)
        {
            calls.Add("record");
            return startException is null ? Task.CompletedTask : Task.FromException(startException);
        }

        public Task<RecordingResult?> StopAndFinalizeIfStartedAsync(CancellationToken cancellationToken)
        {
            calls.Add("finalize-if-started");
            return finalizeException is null
                ? Task.FromResult<RecordingResult?>(null)
                : Task.FromException<RecordingResult?>(finalizeException);
        }
    }

    private sealed class RecordingStore(List<string> calls) : IRecordingStore
    {
        public Task<RecordingTarget> PrepareAsync(MeetingJoinRequest request, CancellationToken cancellationToken)
        {
            calls.Add("store");
            return Task.FromResult(new RecordingTarget("C:\\Videos\\meeting.mp4"));
        }
    }
}
