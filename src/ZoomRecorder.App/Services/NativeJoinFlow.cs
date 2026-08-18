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
    private readonly NativeRecordingSession recording;
    private readonly FinalizationGate finalization = new();
    public event EventHandler<RecordingResult>? RecordingCompleted;
    public event EventHandler<string>? FinalizationFailed;

    public NativeJoinFlow(NativeSession session)
    {
        this.session = session;
        recording = new NativeRecordingSession(session);
        session.NativeEvent += NativeEventReceived;
    }

    public async Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken)
    {
        finalization.Reset();
        var orchestrator = new MeetingOrchestrator(
            new NativeMeetingClient(session),
            recording,
            new LocalRecordingStore(),
            new MeetingLifecycle());
        await orchestrator.JoinAndRecordAsync(request, cancellationToken);
    }

    private void NativeEventReceived(object? sender, string json)
    {
        if (ShouldFinalize(json)) _ = FinalizeFromNativeAsync();
    }

    internal static bool ShouldFinalize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("type", out var type)) return false;
            return type.GetString() is "meeting_ended" or "capture_ended";
        }
        catch (JsonException) { return false; }
    }

    public Task StopAndSaveAsync() => FinalizeAsync();

    private async Task FinalizeFromNativeAsync()
    {
        try { await FinalizeAsync(); }
        catch { }
    }

    private async Task FinalizeAsync()
    {
        if (!finalization.TryBegin()) return;
        await Task.Yield();
        try
        {
            var result = await recording.StopAndFinalizeIfStartedAsync(CancellationToken.None);
            if (result is not null) RecordingCompleted?.Invoke(this, result);
        }
        catch (Exception exception)
        {
            finalization.Reset();
            FinalizationFailed?.Invoke(this, exception.Message);
            throw;
        }
    }
}
