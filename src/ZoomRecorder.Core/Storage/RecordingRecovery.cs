namespace ZoomRecorder.Core.Storage;

public static class RecordingRecovery
{
    public static bool IsCandidate(string path) => string.Equals(Path.GetExtension(path), ".partial", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Enumerate(string recordingDirectory)
    {
        if (!Directory.Exists(recordingDirectory)) return [];
        return Directory.EnumerateFiles(recordingDirectory, "*.partial", SearchOption.TopDirectoryOnly)
            .Where(IsCandidate).OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
    }
}
