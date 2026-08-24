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
    public async Task Lost_prejoin_window_is_replaced_without_finalizing()
    {
        var detector = new HandoffDetector((nint)84);
        var recording = new FakeRecording([]);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore([]),
            new FakeLauncher([]),
            detector,
            recording);
        await flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None);

        flow.HandleNativeEvent("{\"type\":\"capture_window_lost\"}");
        await recording.Replaced.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([(nint)84], recording.ReplacedHandles);
        Assert.Equal((nint)42, detector.ExcludedHandle);
        Assert.Equal(0, recording.StopCount);
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
    public async Task Missing_replacement_finalizes_exactly_once()
    {
        var detector = new HandoffDetector(new ZoomWindowTimeoutException());
        var recording = new FakeRecording([]);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore([]), new FakeLauncher([]), detector, recording);
        await flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None);

        flow.HandleNativeEvent("{\"type\":\"capture_window_lost\"}");

        Assert.Equal(0, recording.StopCount);
        await recording.Finalized.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, recording.StopCount);
    }

    [Fact]
    public async Task Duplicate_window_loss_starts_only_one_handoff()
    {
        var replacement = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new HandoffDetector(replacement);
        var recording = new FakeRecording([]);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore([]), new FakeLauncher([]), detector, recording);
        await flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None);

        flow.HandleNativeEvent("{\"type\":\"capture_window_lost\"}");
        flow.HandleNativeEvent("{\"type\":\"capture_window_lost\"}");
        await detector.HandoffStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, detector.CallCount);
        replacement.SetResult((nint)84);
        await recording.Replaced.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Loss_raised_during_reattach_starts_a_followup_handoff()
    {
        var detector = new HandoffDetector((nint)84);
        var recording = new FakeRecording([]);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore([]), new FakeLauncher([]), detector, recording);
        recording.OnReplace = () => flow.HandleNativeEvent("{\"type\":\"capture_window_lost\"}");
        await flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None);

        flow.HandleNativeEvent("{\"type\":\"capture_window_lost\"}");
        await recording.TwoReplacements.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(3, detector.CallCount);
    }

    [Fact]
    public async Task Manual_stop_cancels_handoff_and_finalizes_once()
    {
        var detector = new HandoffDetector();
        var recording = new FakeRecording([]);
        var flow = new ExternalZoomJoinFlow(
            new FakeStore([]), new FakeLauncher([]), detector, recording);
        await flow.JoinAndRecordAsync(new MeetingJoinRequest("1234567890", null), CancellationToken.None);
        flow.HandleNativeEvent("{\"type\":\"capture_window_lost\"}");
        await detector.HandoffStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await flow.StopAndSaveAsync();
        await detector.HandoffCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));

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

    private sealed class HandoffDetector : IZoomWindowDetector
    {
        private readonly Task<nint>? replacement;
        public int CallCount { get; private set; }
        public nint ExcludedHandle { get; private set; }
        public TaskCompletionSource HandoffStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HandoffCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HandoffDetector(nint replacementHandle) => replacement = Task.FromResult(replacementHandle);
        public HandoffDetector(Exception exception) => replacement = Task.FromException<nint>(exception);
        public HandoffDetector(TaskCompletionSource<nint> replacementSource) => replacement = replacementSource.Task;
        public HandoffDetector() { }

        public async Task<nint> WaitForMeetingWindowAsync(
            TimeSpan timeout, CancellationToken cancellationToken, nint excludedHandle = default)
        {
            CallCount++;
            if (CallCount == 1) return (nint)42;
            ExcludedHandle = excludedHandle;
            HandoffStarted.TrySetResult();
            if (replacement is not null) return await replacement.WaitAsync(cancellationToken);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { HandoffCancelled.TrySetResult(); throw; }
            throw new InvalidOperationException();
        }
    }

    private sealed class FakeRecording(List<string> events, long resultByteSize = 1) : IWindowRecordingSession
    {
        public RecordingTarget? Target { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public List<nint> ReplacedHandles { get; } = [];
        public TaskCompletionSource Replaced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoReplacements { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnReplace { get; set; }
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
            Replaced.TrySetResult();
            if (ReplacedHandles.Count == 2) TwoReplacements.TrySetResult();
            var callback = OnReplace;
            OnReplace = null;
            callback?.Invoke();
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
