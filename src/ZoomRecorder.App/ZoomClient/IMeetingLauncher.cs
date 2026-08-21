namespace ZoomRecorder.App.ZoomClient;

public interface IMeetingLauncher
{
    Task OpenAsync(Uri meetingUri, CancellationToken cancellationToken);
}

internal interface IWindowsShell
{
    void Open(Uri uri);
}
