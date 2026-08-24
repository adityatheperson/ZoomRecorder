using System.Text.Json;
using ZoomRecorder.App.Interop;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.ZoomClient;
using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.Services;

internal sealed class ExternalZoomJoinFlow : IJoinFlow
{
    private static readonly TimeSpan MeetingWindowTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WindowHandoffTimeout = TimeSpan.FromSeconds(15);
    private readonly IRecordingStore recordingStore;
    private readonly IMeetingLauncher launcher;
    private readonly IZoomWindowDetector detector;
    private readonly IWindowRecordingSession recording;
    private readonly FinalizationGate finalization = new();
    private readonly object handoffLock = new();
    private CancellationTokenSource? handoffCancellation;
    private Task? handoffTask;
    private bool pendingWindowLoss;
    private nint currentMeetingWindow;

    public event EventHandler<RecordingResult>? RecordingCompleted;
    public event EventHandler<string>? FinalizationFailed;
    public string? CurrentMeetingId { get; private set; }

    public ExternalZoomJoinFlow(NativeSession session)
        : this(
            new LocalRecordingStore(),
            new WindowsMeetingLauncher(),
            new ZoomWindowDetector(new Win32ZoomWindowEnumerator()),
            new NativeRecordingSession(session))
    {
        session.NativeEvent += (_, json) => HandleNativeEvent(json);
    }

    internal ExternalZoomJoinFlow(
        IRecordingStore recordingStore,
        IMeetingLauncher launcher,
        IZoomWindowDetector detector,
        IWindowRecordingSession recording)
    {
        this.recordingStore = recordingStore;
        this.launcher = launcher;
        this.detector = detector;
        this.recording = recording;
    }

    public async Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CancelHandoff();
        finalization.Reset();
        CurrentMeetingId = null;
        currentMeetingWindow = nint.Zero;
        pendingWindowLoss = false;
        var target = await recordingStore.PrepareAsync(request, cancellationToken);
        try
        {
            await launcher.OpenAsync(ZoomMeetingLaunchUri.Create(request), cancellationToken);
            var meetingWindow = await detector.WaitForMeetingWindowAsync(MeetingWindowTimeout, cancellationToken);
            await recording.StartAsync(target, meetingWindow, cancellationToken);
            currentMeetingWindow = meetingWindow;
            CurrentMeetingId = request.MeetingId.Trim();
        }
        catch
        {
            DeleteEmptyTarget(target.Path);
            throw;
        }
    }

    internal void HandleNativeEvent(string json)
    {
        if (IsWindowLost(json)) StartHandoff();
    }

    internal static bool IsWindowLost(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "capture_window_lost";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public Task StopAndSaveAsync()
    {
        CancelHandoff();
        return FinalizeAsync();
    }

    private void StartHandoff()
    {
        lock (handoffLock)
        {
            if (currentMeetingWindow == nint.Zero) return;
            if (handoffTask is { IsCompleted: false })
            {
                pendingWindowLoss = true;
                return;
            }
            pendingWindowLoss = false;
            handoffCancellation?.Dispose();
            handoffCancellation = new CancellationTokenSource();
            var cancellation = handoffCancellation;
            var lostWindow = currentMeetingWindow;
            handoffTask = Task.Run(() => HandoffAsync(lostWindow, cancellation));
        }
    }

    private async Task HandoffAsync(nint lostWindow, CancellationTokenSource cancellation)
    {
        try
        {
            var replacement = await detector.WaitForMeetingWindowAsync(
                WindowHandoffTimeout, cancellation.Token, lostWindow).ConfigureAwait(false);
            await recording.ReplaceWindowAsync(replacement, cancellation.Token).ConfigureAwait(false);
            lock (handoffLock) currentMeetingWindow = replacement;
        }
        catch (ZoomWindowTimeoutException)
        {
            try { await FinalizeAsync().ConfigureAwait(false); }
            catch { }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            FinalizationFailed?.Invoke(this, exception.Message);
            try { await FinalizeAsync().ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            var restart = false;
            lock (handoffLock)
            {
                if (ReferenceEquals(handoffCancellation, cancellation))
                {
                    handoffTask = null;
                    restart = pendingWindowLoss && currentMeetingWindow != nint.Zero;
                    pendingWindowLoss = false;
                }
            }
            if (restart) StartHandoff();
        }
    }

    private void CancelHandoff()
    {
        lock (handoffLock) handoffCancellation?.Cancel();
    }

    private async Task FinalizeAsync()
    {
        if (!finalization.TryBegin()) return;
        lock (handoffLock)
        {
            currentMeetingWindow = nint.Zero;
            pendingWindowLoss = false;
            handoffCancellation?.Cancel();
        }
        try
        {
            var result = await recording.StopAndFinalizeIfStartedAsync(CancellationToken.None);
            if (result is not null && result.ByteSize > 0)
            {
                RecordingCompleted?.Invoke(this, result);
            }
            else if (result is not null)
            {
                DeleteEmptyTarget(result.Path);
            }
        }
        catch (Exception exception)
        {
            finalization.Reset();
            FinalizationFailed?.Invoke(this, exception.Message);
            throw;
        }
    }

    private static void DeleteEmptyTarget(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
