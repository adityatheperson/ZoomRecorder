namespace ZoomRecorder.Core.Storage;

public static class RecordingRecovery
{
    public static bool IsCandidate(string path) =>
        path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Enumerate(string recordingDirectory)
    {
        if (!Directory.Exists(recordingDirectory)) return [];
        return Directory.EnumerateFiles(recordingDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsCandidate).OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
    }
}
