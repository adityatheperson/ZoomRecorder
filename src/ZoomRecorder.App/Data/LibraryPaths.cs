namespace ZoomRecorder.App.Data;

public sealed class LibraryPaths
{
    public LibraryPaths(
        string databasePath,
        string artifactsRoot,
        string jobsRoot,
        string? recordingsRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsRoot);

        DatabasePath = Path.GetFullPath(databasePath);
        ArtifactsRoot = Path.GetFullPath(artifactsRoot);
        JobsRoot = Path.GetFullPath(jobsRoot);
        RecordingsRoot = Path.GetFullPath(recordingsRoot ?? DefaultRecordingsRoot());
    }

    public string DatabasePath { get; }
    public string ArtifactsRoot { get; }
    public string JobsRoot { get; }
    public string RecordingsRoot { get; }

    public static LibraryPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new LibraryPaths(
            Path.Combine(localAppData, "ZoomRecorder", "library.db"),
            DefaultArtifactsRoot(),
            Path.Combine(localAppData, "ZoomRecorder", "jobs"),
            DefaultRecordingsRoot());
    }

    internal static string DefaultRecordingsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "Meeting Recordings");

    internal static string DefaultArtifactsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents", "Zoom Recorder", "Classes");
}
