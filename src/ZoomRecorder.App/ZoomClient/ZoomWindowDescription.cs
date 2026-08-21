namespace ZoomRecorder.App.ZoomClient;

public sealed record ZoomWindowDescription(
    nint Handle,
    int ProcessId,
    string ProcessName,
    string ClassName,
    string Title,
    bool IsVisible,
    bool IsMinimized,
    int Width,
    int Height);
