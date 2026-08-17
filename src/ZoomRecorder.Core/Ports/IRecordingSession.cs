namespace ZoomRecorder.Core.Ports;

public sealed record RecordingTarget(string Path);

public sealed record RecordingResult(string Path, TimeSpan Duration, long ByteSize);

public interface IRecordingSession
{
    Task StartAsync(RecordingTarget target, CancellationToken cancellationToken);
    Task<RecordingResult?> StopAndFinalizeIfStartedAsync(CancellationToken cancellationToken);
}

public sealed class RecordingStartException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
