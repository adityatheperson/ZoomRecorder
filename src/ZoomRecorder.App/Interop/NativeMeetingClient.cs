using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.Interop;

internal sealed class NativeMeetingClient(NativeSession session) : IMeetingClient
{
    public Task PrepareAsync(MeetingJoinRequest request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); session.Prepare(request); return Task.CompletedTask; }
    public Task EnterAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); session.Enter(); return Task.CompletedTask; }
    public Task CancelPreparedMeetingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
