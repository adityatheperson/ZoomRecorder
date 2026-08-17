using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.Interop;

internal sealed class NativeRecordingSession(NativeSession session) : IRecordingSession
{
    private RecordingTarget? target;
    public Task StartAsync(RecordingTarget value, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); session.StartRecording(value.Path); target = value; return Task.CompletedTask; }
    public Task<RecordingResult?> StopAndFinalizeIfStartedAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); if (target is null) return Task.FromResult<RecordingResult?>(null); session.FinalizeRecording(); var result = new RecordingResult(target.Path, TimeSpan.Zero, File.Exists(target.Path) ? new FileInfo(target.Path).Length : 0); return Task.FromResult<RecordingResult?>(result); }
}
