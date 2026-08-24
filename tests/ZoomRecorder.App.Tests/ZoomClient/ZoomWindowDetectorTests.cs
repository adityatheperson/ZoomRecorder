using ZoomRecorder.App.ZoomClient;

namespace ZoomRecorder.App.Tests.ZoomClient;

public sealed class ZoomWindowDetectorTests
{
    [Fact]
    public async Task Requires_three_consecutive_observations_of_the_same_handle()
    {
        var meeting = Window((nint)7);
        var enumerator = new ScriptedEnumerator([], [meeting], [meeting], [meeting]);
        var detector = new ZoomWindowDetector(enumerator, TimeProvider.System, TimeSpan.FromMilliseconds(1));

        var handle = await detector.WaitForMeetingWindowAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal((nint)7, handle);
        Assert.Equal(4, enumerator.CallCount);
    }

    [Fact]
    public async Task A_changed_handle_restarts_the_stability_count()
    {
        var first = Window((nint)7);
        var second = Window((nint)8);
        var enumerator = new ScriptedEnumerator([first], [first], [second], [second], [second]);
        var detector = new ZoomWindowDetector(enumerator, TimeProvider.System, TimeSpan.FromMilliseconds(1));

        var handle = await detector.WaitForMeetingWindowAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal((nint)8, handle);
        Assert.Equal(5, enumerator.CallCount);
    }

    [Fact]
    public async Task Excluded_window_is_ignored_until_a_replacement_is_stable()
    {
        var oldWindow = Window((nint)7);
        var replacement = Window((nint)8);
        var detector = new ZoomWindowDetector(
            new ScriptedEnumerator([oldWindow], [oldWindow, replacement], [replacement], [replacement]),
            TimeProvider.System,
            TimeSpan.FromMilliseconds(1));

        var handle = await detector.WaitForMeetingWindowAsync(
            TimeSpan.FromSeconds(1), CancellationToken.None, (nint)7);

        Assert.Equal((nint)8, handle);
    }

    [Fact]
    public async Task Ambiguity_throws_a_typed_exception()
    {
        var detector = new ZoomWindowDetector(
            new ScriptedEnumerator([Window((nint)7), Window((nint)8)]),
            TimeProvider.System,
            TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<ZoomWindowAmbiguousException>(() =>
            detector.WaitForMeetingWindowAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_throws_a_typed_exception()
    {
        var detector = new ZoomWindowDetector(
            new ScriptedEnumerator([]),
            TimeProvider.System,
            TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<ZoomWindowTimeoutException>(() =>
            detector.WaitForMeetingWindowAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var detector = new ZoomWindowDetector(new ScriptedEnumerator([]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            detector.WaitForMeetingWindowAsync(TimeSpan.FromMinutes(15), cancellation.Token));
    }

    private static ZoomWindowDescription Window(nint handle) =>
        new(handle, 42, "Zoom", "ZPContentViewWndClass", "Zoom Meeting", true, false, 1400, 900);

    private sealed class ScriptedEnumerator(params IReadOnlyList<ZoomWindowDescription>[] observations)
        : IZoomWindowEnumerator
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<ZoomWindowDescription> Enumerate()
        {
            var index = Math.Min(CallCount, observations.Length - 1);
            CallCount++;
            return observations.Length == 0 ? [] : observations[index];
        }
    }
}
