namespace ZoomRecorder.App.Services;

internal sealed class FinalizationGate
{
    private int started;

    public bool TryBegin() => Interlocked.Exchange(ref started, 1) == 0;

    public void Reset() => Interlocked.Exchange(ref started, 0);
}
