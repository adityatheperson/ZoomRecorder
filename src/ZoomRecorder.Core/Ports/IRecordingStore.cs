using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.Core.Ports;

public interface IRecordingStore
{
    Task<RecordingTarget> PrepareAsync(MeetingJoinRequest request, CancellationToken cancellationToken);
}
