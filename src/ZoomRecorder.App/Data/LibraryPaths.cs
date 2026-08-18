namespace ZoomRecorder.App.Data;

public sealed class LibraryPaths
{
    public LibraryPaths(string databasePath, string artifactsRoot, string jobsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsRoot);

        DatabasePath = Path.GetFullPath(databasePath);
        ArtifactsRoot = Path.GetFullPath(artifactsRoot);
        JobsRoot = Path.GetFullPath(jobsRoot);
    }

    public string DatabasePath { get; }
    public string ArtifactsRoot { get; }
    public string JobsRoot { get; }

    public static LibraryPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new LibraryPaths(
            Path.Combine(localAppData, "ZoomRecorder", "library.db"),
            Path.Combine(userProfile, "Documents", "Zoom Recorder", "Classes"),
            Path.Combine(localAppData, "ZoomRecorder", "jobs"));
    }
}
