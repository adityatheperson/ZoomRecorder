namespace ZoomRecorder.App.Services;

internal static class WindowIconPath
{
    internal static string Resolve(string baseDirectory) =>
        Path.Combine(baseDirectory, "Assets", "ZoomRecorder.ico");
}
