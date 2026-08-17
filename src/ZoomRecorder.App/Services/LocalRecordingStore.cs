using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Ports;
using ZoomRecorder.Core.Storage;

namespace ZoomRecorder.App.Services;

internal sealed class LocalRecordingStore : IRecordingStore
{
    public Task<RecordingTarget> PrepareAsync(MeetingJoinRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var directory = Path.Combine(videos, "Meeting Recordings");
        Directory.CreateDirectory(directory);
        var path = RecordingPathFactory.Create(directory, $"Zoom {request.MeetingId}", DateTimeOffset.Now, File.Exists);
        return Task.FromResult(new RecordingTarget(path));
    }
}
