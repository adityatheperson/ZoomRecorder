using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace ZoomRecorder.App.ZoomClient;

public sealed class ZoomWorkplaceUnavailableException(Exception? innerException = null)
    : InvalidOperationException("Zoom Workplace could not be opened. Install Zoom Workplace and try again.", innerException);

public sealed class MeetingLaunchException(Exception? innerException = null)
    : InvalidOperationException("Windows could not open the Zoom meeting link.", innerException);

internal interface IZoomInstallationLocator
{
    bool IsAvailable();
}

internal sealed class ZoomInstallationLocator : IZoomInstallationLocator
{
    public bool IsAvailable()
    {
        if (HasRegisteredProtocol()) return true;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zoom", "bin", "Zoom.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zoom", "bin", "Zoom.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zoom", "bin", "Zoom.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Zoom", "bin", "Zoom.exe")
        };
        return candidates.Any(File.Exists);
    }

    private static bool HasRegisteredProtocol()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Classes\zoommtg\shell\open\command",
                string.Empty,
                null) is string command && !string.IsNullOrWhiteSpace(command);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

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
    private readonly IZoomInstallationLocator installationLocator;

    public WindowsMeetingLauncher() : this(new ProcessWindowsShell(), new ZoomInstallationLocator()) { }

    internal WindowsMeetingLauncher(IWindowsShell shell, IZoomInstallationLocator installationLocator)
    {
        this.shell = shell ?? throw new ArgumentNullException(nameof(shell));
        this.installationLocator = installationLocator ?? throw new ArgumentNullException(nameof(installationLocator));
    }

    public Task OpenAsync(Uri meetingUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meetingUri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!installationLocator.IsAvailable()) throw new ZoomWorkplaceUnavailableException();
        try
        {
            shell.Open(meetingUri);
            return Task.CompletedTask;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1155)
        {
            throw new ZoomWorkplaceUnavailableException(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MeetingLaunchException(exception);
        }
    }
}
