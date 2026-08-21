using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.Interop;

internal interface IWindowRecordingSession
{
    Task StartAsync(RecordingTarget target, nint meetingWindow, CancellationToken cancellationToken);
    Task<RecordingResult?> StopAndFinalizeIfStartedAsync(CancellationToken cancellationToken);
}

internal sealed class NativeRecordingSession(NativeSession session) : IWindowRecordingSession
{
    private RecordingTarget? target;
    private DateTimeOffset startedAt;
    public Task StartAsync(RecordingTarget value, nint meetingWindow, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); session.StartRecording(value.Path, meetingWindow); target = value; startedAt = DateTimeOffset.UtcNow; return Task.CompletedTask; }
    public Task<RecordingResult?> StopAndFinalizeIfStartedAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); if (target is null) return Task.FromResult<RecordingResult?>(null); session.FinalizeRecording(); var result = new RecordingResult(target.Path, DateTimeOffset.UtcNow - startedAt, File.Exists(target.Path) ? new FileInfo(target.Path).Length : 0); target = null; return Task.FromResult<RecordingResult?>(result); }
}
