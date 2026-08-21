namespace ZoomRecorder.App.ZoomClient;

public sealed class ZoomWindowAmbiguousException()
    : InvalidOperationException("More than one Zoom meeting window is available.");

public sealed class ZoomWindowTimeoutException()
    : TimeoutException("A Zoom meeting window did not become ready before the timeout.");

public interface IZoomWindowDetector
{
    Task<nint> WaitForMeetingWindowAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ZoomWindowDetector : IZoomWindowDetector
{
    private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromMilliseconds(250);
    private readonly IZoomWindowEnumerator enumerator;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pollingInterval;

    public ZoomWindowDetector(
        IZoomWindowEnumerator enumerator,
        TimeProvider? timeProvider = null,
        TimeSpan? pollingInterval = null)
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pollingInterval = pollingInterval ?? DefaultPollingInterval;
        if (this.pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollingInterval));
        }
    }

    public async Task<nint> WaitForMeetingWindowAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCancellation = new CancellationTokenSource(timeout, timeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        nint lastHandle = nint.Zero;
        var stableObservations = 0;

        try
        {
            while (true)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                var selection = ZoomWindowSelection.Select(enumerator.Enumerate());
                if (selection.Kind == ZoomWindowSelectionKind.Ambiguous)
                {
                    throw new ZoomWindowAmbiguousException();
                }

                if (selection.Kind == ZoomWindowSelectionKind.Selected)
                {
                    stableObservations = selection.Handle == lastHandle ? stableObservations + 1 : 1;
                    lastHandle = selection.Handle;
                    if (stableObservations == 3)
                    {
                        return selection.Handle;
                    }
                }
                else
                {
                    lastHandle = nint.Zero;
                    stableObservations = 0;
                }

                await Task.Delay(pollingInterval, timeProvider, linkedCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ZoomWindowTimeoutException();
        }
    }
}
