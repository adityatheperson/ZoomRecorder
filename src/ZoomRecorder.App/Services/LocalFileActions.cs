using System.Diagnostics;

namespace ZoomRecorder.App.Services;

internal static class LocalFileActions
{
    public static void Open(string recordingPath, string recordingDirectory)
    {
        Validate(recordingPath, recordingDirectory);
        Process.Start(new ProcessStartInfo(recordingPath) { UseShellExecute = true });
    }

    public static void SelectInFolder(string recordingPath, string recordingDirectory)
    {
        Validate(recordingPath, recordingDirectory);
        Process.Start("explorer.exe", $"/select,\"{recordingPath}\"");
    }

    private static void Validate(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The recording path is outside the configured recording folder.");
    }
}
