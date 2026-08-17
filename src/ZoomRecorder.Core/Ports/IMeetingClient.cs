using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.Core.Ports;

public interface IMeetingClient
{
    Task PrepareAsync(MeetingJoinRequest request, CancellationToken cancellationToken);
    Task EnterAsync(CancellationToken cancellationToken);
    Task CancelPreparedMeetingAsync(CancellationToken cancellationToken);
}
