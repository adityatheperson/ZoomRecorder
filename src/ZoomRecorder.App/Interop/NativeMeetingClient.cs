using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.Interop;

internal sealed class NativeMeetingClient(NativeSession session) : IMeetingClient
{
    public Task PrepareAsync(MeetingJoinRequest request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); session.Prepare(request); return Task.CompletedTask; }
    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new MeetingEntryAwaiter(TimeSpan.FromSeconds(30));
        EventHandler<string> handler = (_, json) => waiter.Observe(json);
        session.NativeEvent += handler;
        try
        {
            session.Enter();
            await waiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.NativeEvent -= handler;
        }
    }
    public Task CancelPreparedMeetingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
