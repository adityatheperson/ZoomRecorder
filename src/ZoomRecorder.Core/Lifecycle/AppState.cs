namespace ZoomRecorder.Core.Lifecycle;

public enum AppState
{
    ReadyToJoin,
    PreparingMeeting,
    StartingRecording,
    RecordingReady,
    InMeetingRecording,
    FinalizingRecording,
    RecordingComplete,
    RecoverableError
}
