namespace ZoomRecorder.App.LocalTranscription;

internal sealed record WhisperWorkerRequest(
    string ModelPath,
    string WavPath,
    string OutputBasePath);

internal sealed record WhisperWorkerResult(
    string JsonPath,
    bool UsedCpuFallback);

internal sealed record WhisperWorkerLaunch(
    string WorkerPath,
    IReadOnlyList<string> Arguments);

internal interface IWhisperWorkerRunner
{
    Task<WhisperWorkerResult> RunAsync(
        WhisperWorkerRequest request,
        CancellationToken cancellationToken);
}
