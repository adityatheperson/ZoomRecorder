namespace ZoomRecorder.Core.Lifecycle;

public sealed class MeetingLifecycle
{
    public AppState Current { get; private set; } = AppState.ReadyToJoin;

    public string? LastError { get; private set; }

    public AppState Apply(AppEvent appEvent)
    {
        ArgumentNullException.ThrowIfNull(appEvent);

        if (appEvent is RequiredComponentFailed failure && Current is not AppState.ReadyToJoin and not AppState.RecordingComplete)
        {
            LastError = failure.Message;
            return Current = AppState.RecoverableError;
        }

        if (Current == AppState.FinalizingRecording && appEvent is MeetingEnded)
        {
            return Current;
        }

        var next = (Current, appEvent) switch
        {
            (AppState.ReadyToJoin, JoinRequested) => AppState.PreparingMeeting,
            (AppState.PreparingMeeting, MeetingPrepared) => AppState.StartingRecording,
            (AppState.StartingRecording, RecordingStarted) => AppState.RecordingReady,
            (AppState.RecordingReady, MeetingEntered) => AppState.InMeetingRecording,
            (AppState.InMeetingRecording, MeetingEnded) => AppState.FinalizingRecording,
            (AppState.FinalizingRecording, RecordingFinalized) => AppState.RecordingComplete,
            _ => throw new InvalidStateTransitionException(Current, appEvent.GetType())
        };

        return Current = next;
    }
}

public sealed class InvalidStateTransitionException(AppState state, Type eventType)
    : InvalidOperationException($"Cannot apply {eventType.Name} while in {state}.");
