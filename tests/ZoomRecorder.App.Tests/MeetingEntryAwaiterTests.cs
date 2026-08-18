using ZoomRecorder.App.Interop;

namespace ZoomRecorder.App.Tests;

public sealed class MeetingEntryAwaiterTests
{
    [Fact]
    public async Task CompletesOnlyWhenMeetingWindowIsReady()
    {
        var waiter = new MeetingEntryAwaiter(TimeSpan.FromSeconds(1));
        waiter.Observe("{\"type\":\"meeting_entered\"}");
        waiter.Observe("{\"type\":\"meeting_window_ready\"}");

        await waiter.WaitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReportsNativeJoinFailureMessage()
    {
        var waiter = new MeetingEntryAwaiter(TimeSpan.FromSeconds(1));
        waiter.Observe("{\"type\":\"failed\",\"message\":\"Zoom Join failed (SDK error 8)\"}");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => waiter.WaitAsync(CancellationToken.None));

        Assert.Contains("SDK error 8", error.Message);
    }

    [Fact]
    public async Task TimesOutWhenNoMeetingWindowAppears()
    {
        var waiter = new MeetingEntryAwaiter(TimeSpan.FromMilliseconds(10));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => waiter.WaitAsync(CancellationToken.None));

        Assert.Contains("did not appear", error.Message);
    }
}
