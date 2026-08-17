using ZoomRecorder.App.Interop;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.Core.Lifecycle;
using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Orchestration;
using ZoomRecorder.Core.Ports;
using System.Text.Json;

namespace ZoomRecorder.App.Services;

internal sealed class NativeJoinFlow : IJoinFlow
{
    private readonly NativeSession session;
    private readonly Func<nint> meetingHost;
    private readonly NativeRecordingSession recording;
    private int finalizing;
    public event EventHandler<RecordingResult>? RecordingCompleted;

    public NativeJoinFlow(NativeSession session, Func<nint> meetingHost)
    {
        this.session = session;
        this.meetingHost = meetingHost;
        recording = new NativeRecordingSession(session);
        session.NativeEvent += NativeEventReceived;
    }

    public async Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken)
    {
        session.SetMeetingHost(meetingHost());
        var orchestrator = new MeetingOrchestrator(
            new NativeMeetingClient(session),
            recording,
            new LocalRecordingStore(),
            new MeetingLifecycle());
        await orchestrator.JoinAndRecordAsync(request, cancellationToken);
    }

    private void NativeEventReceived(object? sender, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "meeting_ended" && Interlocked.Exchange(ref finalizing, 1) == 0)
                _ = FinalizeAsync();
        }
        catch (JsonException) { }
    }

    private async Task FinalizeAsync()
    {
        var result = await recording.StopAndFinalizeIfStartedAsync(CancellationToken.None);
        if (result is not null) RecordingCompleted?.Invoke(this, result);
    }
}
