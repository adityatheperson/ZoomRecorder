using ZoomRecorder.Core.Lifecycle;
using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.Core.Orchestration;

public sealed class MeetingOrchestrator(
    IMeetingClient meeting,
    IRecordingSession recording,
    IRecordingStore store,
    MeetingLifecycle lifecycle)
{
    private readonly StatusSource _status = new();

    public AppState State => lifecycle.Current;

    public IObservable<MeetingStatus> Status => _status;

    public async Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        lifecycle.Apply(new JoinRequested(request));
        _status.Publish(new MeetingStatus(lifecycle.Current));

        var target = await store.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        await meeting.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        lifecycle.Apply(new MeetingPrepared());

        try
        {
            await recording.StartAsync(target, cancellationToken).ConfigureAwait(false);
            lifecycle.Apply(new RecordingStarted());
            await meeting.EnterAsync(cancellationToken).ConfigureAwait(false);
            lifecycle.Apply(new MeetingEntered());
            _status.Publish(new MeetingStatus(lifecycle.Current));
        }
        catch (Exception exception)
        {
            await recording.StopAndFinalizeIfStartedAsync(CancellationToken.None).ConfigureAwait(false);
            await meeting.CancelPreparedMeetingAsync(CancellationToken.None).ConfigureAwait(false);
            lifecycle.Apply(new RequiredComponentFailed(exception.Message));
            _status.Publish(MeetingStatus.Failed(exception.Message));
            throw;
        }
    }

    private sealed class StatusSource : IObservable<MeetingStatus>
    {
        private readonly List<IObserver<MeetingStatus>> _observers = [];

        public IDisposable Subscribe(IObserver<MeetingStatus> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            _observers.Add(observer);
            return new Subscription(_observers, observer);
        }

        public void Publish(MeetingStatus status)
        {
            foreach (var observer in _observers.ToArray())
            {
                observer.OnNext(status);
            }
        }

        private sealed class Subscription(List<IObserver<MeetingStatus>> observers, IObserver<MeetingStatus> observer) : IDisposable
        {
            public void Dispose() => observers.Remove(observer);
        }
    }
}
