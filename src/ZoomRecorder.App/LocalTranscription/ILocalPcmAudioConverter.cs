using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.LocalTranscription;

internal interface ILocalPcmAudioConverter
{
    Task<string> ConvertAsync(
        AudioChunk chunk,
        string jobDirectory,
        CancellationToken cancellationToken);
}
