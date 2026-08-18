namespace ZoomRecorder.Core.Processing;

public enum ProcessingState
{
    ReadyToProcess,
    PreparingAudio,
    Transcribing,
    GeneratingStudyPackage,
    UpdatingClassGuide,
    Completed,
    NeedsAttention,
    Cancelled
}
