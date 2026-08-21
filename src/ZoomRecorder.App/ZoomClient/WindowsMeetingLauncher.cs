using System.ComponentModel;
using System.Diagnostics;

namespace ZoomRecorder.App.ZoomClient;

public sealed class ZoomWorkplaceUnavailableException()
    : InvalidOperationException("Zoom Workplace could not be opened. Install Zoom Workplace and try again.");

public sealed class MeetingLaunchException()
    : InvalidOperationException("Windows could not open the Zoom meeting link.");

internal sealed class ProcessWindowsShell : IWindowsShell
{
    public void Open(Uri uri) => Process.Start(new ProcessStartInfo
    {
        FileName = uri.AbsoluteUri,
        UseShellExecute = true
    });
}

public sealed class WindowsMeetingLauncher : IMeetingLauncher
{
    private readonly IWindowsShell shell;

    public WindowsMeetingLauncher() : this(new ProcessWindowsShell()) { }

    internal WindowsMeetingLauncher(IWindowsShell shell) =>
        this.shell = shell ?? throw new ArgumentNullException(nameof(shell));

    public Task OpenAsync(Uri meetingUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meetingUri);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            shell.Open(meetingUri);
            return Task.CompletedTask;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1155)
        {
            throw new ZoomWorkplaceUnavailableException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MeetingLaunchException { Source = exception.GetType().Name };
        }
    }
}
