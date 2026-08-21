using ZoomRecorder.App.ZoomClient;

namespace ZoomRecorder.App.Tests.ZoomClient;

public sealed class WindowsMeetingLauncherTests
{
    [Fact]
    public async Task Opens_the_exact_https_uri_through_the_shell()
    {
        var shell = new FakeShell();
        var launcher = new WindowsMeetingLauncher(shell);
        var uri = new Uri("https://zoom.us/j/1234567890?pwd=a%20b");

        await launcher.OpenAsync(uri, CancellationToken.None);

        Assert.Equal(uri, shell.OpenedUri);
    }

    [Fact]
    public async Task Missing_protocol_handler_has_a_specific_error()
    {
        var launcher = new WindowsMeetingLauncher(new FakeShell(new System.ComponentModel.Win32Exception(1155)));

        await Assert.ThrowsAsync<ZoomWorkplaceUnavailableException>(() =>
            launcher.OpenAsync(new Uri("https://zoom.us/j/1234567890"), CancellationToken.None));
    }

    [Fact]
    public async Task Other_shell_failures_are_sanitized()
    {
        var launcher = new WindowsMeetingLauncher(new FakeShell(new InvalidOperationException("secret")));

        var error = await Assert.ThrowsAsync<MeetingLaunchException>(() =>
            launcher.OpenAsync(new Uri("https://zoom.us/j/1234567890"), CancellationToken.None));

        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var launcher = new WindowsMeetingLauncher(new FakeShell());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            launcher.OpenAsync(new Uri("https://zoom.us/j/1234567890"), cancellation.Token));
    }

    private sealed class FakeShell(Exception? error = null) : IWindowsShell
    {
        public Uri? OpenedUri { get; private set; }

        public void Open(Uri uri)
        {
            if (error is not null) throw error;
            OpenedUri = uri;
        }
    }
}
