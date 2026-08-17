using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.Core.Lifecycle;

public abstract record AppEvent;

public sealed record JoinRequested(MeetingJoinRequest Request) : AppEvent;
public sealed record MeetingPrepared : AppEvent;
public sealed record RecordingStarted : AppEvent;
public sealed record MeetingEntered : AppEvent;
public sealed record MeetingEnded : AppEvent;
public sealed record RecordingFinalized(string Path, TimeSpan Duration, long ByteSize) : AppEvent;
public sealed record RequiredComponentFailed(string Message) : AppEvent;
