using ZoomRecorder.App.Services;
using ZoomRecorder.App.Interop;
using ZoomRecorder.App.ZoomClient;
using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.Tests;

public sealed class ExternalZoomJoinFlowTests
{
    [Fact]
    public async Task Recording_session_contract_can_replace_the_capture_window()
    {
        IWindowRecordingSession recording = new FakeRecording([]);

        await recording.ReplaceWindowAsync((nint)84, CancellationToken.None);

        Assert.Equal([(nint)84], ((FakeRecording)recording).ReplacedHandles);
    }

    [Fact]
    public async Task Prepares_launches_detects_then_starts_with_the_exact_window()
    {
        var events = new List<string>();
        var recording = new FakeRecording(events);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore(events),
            new FakeLauncher(events),
            new FakeDetector(events, (nint)42),
            recording);

        await flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None);

        Assert.Equal(["prepare", "launch:https://zoom.us/j/1234567890", "detect", "start:42"], events);
        Assert.Equal("1234567890", flow.CurrentMeetingId);
        Assert.Equal("recording.mp4", recording.Target?.Path);
    }

    [Fact]
    public async Task Detection_failure_never_starts_native_capture()
    {
        var recording = new FakeRecording([]);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore([]),
            new FakeLauncher([]),
            new ThrowingDetector(new ZoomWindowTimeoutException()),
            recording);

        await Assert.ThrowsAsync<ZoomWindowTimeoutException>(() =>
            flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None));

        Assert.Equal(0, recording.StartCount);
    }

    [Fact]
    public async Task Capture_end_and_manual_stop_finalize_exactly_once()
    {
        var recording = new FakeRecording([]);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore([]),
            new FakeLauncher([]),
            new FakeDetector([], (nint)42),
            recording);
        var completions = 0;
        flow.RecordingCompleted += (_, _) => completions++;
        await flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None);

        flow.HandleNativeEvent("{\"type\":\"capture_ended\"}");
        await flow.StopAndSaveAsync();
        await recording.Finalized.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, recording.StopCount);
        Assert.Equal(1, completions);
    }

    [Fact]
    public async Task Zero_byte_capture_is_not_published_as_completed()
    {
        var recording = new FakeRecording([], resultByteSize: 0);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore([]), new FakeLauncher([]), new FakeDetector([], (nint)42), recording);
        var completions = 0;
        flow.RecordingCompleted += (_, _) => completions++;
        await flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None);

        await flow.StopAndSaveAsync();

        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task Native_callback_hands_finalization_off_before_stopping_capture()
    {
        var recording = new FakeRecording([]);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore([]), new FakeLauncher([]), new FakeDetector([], (nint)42), recording);
        await flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None);

        flow.HandleNativeEvent("{\"type\":\"capture_ended\"}");

        Assert.Equal(0, recording.StopCount);
        await recording.Finalized.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, recording.StopCount);
    }

    private sealed class FakeStore(List<string> events) : IRecordingStore
    {
        public Task<RecordingTarget> PrepareAsync(MeetingJoinRequest request, CancellationToken cancellationToken)
        {
            events.Add("prepare");
            return Task.FromResult(new RecordingTarget("recording.mp4"));
        }
    }

    private sealed class FakeLauncher(List<string> events) : IMeetingLauncher
    {
        public Task OpenAsync(Uri meetingUri, CancellationToken cancellationToken)
        {
            events.Add($"launch:{meetingUri.AbsoluteUri}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDetector(List<string> events, nint handle) : IZoomWindowDetector
    {
        public Task<nint> WaitForMeetingWindowAsync(TimeSpan timeout, CancellationToken cancellationToken, nint excludedHandle = default)
        {
            events.Add("detect");
            return Task.FromResult(handle);
        }
    }

    private sealed class ThrowingDetector(Exception exception) : IZoomWindowDetector
    {
        public Task<nint> WaitForMeetingWindowAsync(TimeSpan timeout, CancellationToken cancellationToken, nint excludedHandle = default) =>
            Task.FromException<nint>(exception);
    }

    private sealed class FakeRecording(List<string> events, long resultByteSize = 1) : IWindowRecordingSession
    {
        public RecordingTarget? Target { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public List<nint> ReplacedHandles { get; } = [];
        public TaskCompletionSource Finalized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(RecordingTarget target, nint meetingWindow, CancellationToken cancellationToken)
        {
            StartCount++;
            Target = target;
            events.Add($"start:{meetingWindow}");
            return Task.CompletedTask;
        }

        public Task ReplaceWindowAsync(nint meetingWindow, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplacedHandles.Add(meetingWindow);
            events.Add($"replace:{meetingWindow}");
            return Task.CompletedTask;
        }

        public Task<RecordingResult?> StopAndFinalizeIfStartedAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            Finalized.TrySetResult();
            return Task.FromResult<RecordingResult?>(new RecordingResult(Target?.Path ?? "recording.mp4", TimeSpan.Zero, resultByteSize));
        }
    }
}
