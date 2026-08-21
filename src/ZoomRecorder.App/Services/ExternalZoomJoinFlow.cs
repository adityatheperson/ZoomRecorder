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
    private readonly IRecordingStore recordingStore;
    private readonly IMeetingLauncher launcher;
    private readonly IZoomWindowDetector detector;
    private readonly IWindowRecordingSession recording;
    private readonly FinalizationGate finalization = new();

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
        finalization.Reset();
        CurrentMeetingId = null;
        var target = await recordingStore.PrepareAsync(request, cancellationToken);
        try
        {
            await launcher.OpenAsync(ZoomMeetingLaunchUri.Create(request), cancellationToken);
            var meetingWindow = await detector.WaitForMeetingWindowAsync(MeetingWindowTimeout, cancellationToken);
            await recording.StartAsync(target, meetingWindow, cancellationToken);
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
        if (ShouldFinalize(json)) _ = FinalizeFromNativeAsync();
    }

    internal static bool ShouldFinalize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "capture_ended";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public Task StopAndSaveAsync() => FinalizeAsync();

    private Task FinalizeFromNativeAsync() => Task.Run(async () =>
    {
        try { await FinalizeAsync().ConfigureAwait(false); }
        catch { }
    });

    private async Task FinalizeAsync()
    {
        if (!finalization.TryBegin()) return;
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
