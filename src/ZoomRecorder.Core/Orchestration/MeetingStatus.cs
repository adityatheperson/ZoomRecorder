using ZoomRecorder.Core.Lifecycle;

namespace ZoomRecorder.Core.Orchestration;

public sealed record MeetingStatus(AppState State, string? ErrorMessage = null)
{
    public static MeetingStatus Failed(string message) => new(AppState.RecoverableError, message);
}
