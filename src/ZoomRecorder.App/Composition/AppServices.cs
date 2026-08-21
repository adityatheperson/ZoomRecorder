using ZoomRecorder.App.Cloud;
using ZoomRecorder.App.Data;
using ZoomRecorder.App.Deletion;
using ZoomRecorder.App.Media;
using ZoomRecorder.App.Security;
using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Composition;

public sealed class AppServices : IAsyncDisposable
{
    private readonly LibraryDatabase database;
    private readonly HttpClient httpClient;

    private AppServices(
        LibraryDatabase database,
        HttpClient httpClient,
        SqliteLibraryRepository repository,
        ProcessingCoordinator coordinator,
        ICredentialVault credentialVault,
        IAppSettingsStore settings)
    {
        this.database = database;
        this.httpClient = httpClient;
        Repository = repository;
        Coordinator = coordinator;
        CredentialVault = credentialVault;
        Settings = settings;
    }

    public SqliteLibraryRepository Repository { get; }
    public ProcessingCoordinator Coordinator { get; }
    public ICredentialVault CredentialVault { get; }
    public IAppSettingsStore Settings { get; }
    public LibraryDatabase Database => database;

    public static async Task<AppServices> CreateAsync(
        LibraryPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.ArtifactsRoot);
        Directory.CreateDirectory(paths.JobsRoot);
        var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, cancellationToken);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        try
        {
            var repository = new SqliteLibraryRepository(database);
            var jobs = new SqliteProcessingJobStore(database);
            var artifacts = new ArtifactStore(paths.ArtifactsRoot);
            var vault = new WindowsCredentialVault();
            var api = new OpenAiApiClient(http, vault, new OpenAiOptions());
            var coordinator = new ProcessingCoordinator(
                jobs,
                new NativeAudioChunkPreparer(),
                new OpenAiTranscriptionClient(api),
                new OpenAiStudyGenerationClient(api),
                artifacts,
                NativeAudioChunkPreparer.DefaultMaxBytes,
                new WindowsVideoRecycler(),
                repository);
            await coordinator.RecoverAsync(cancellationToken);
            return new AppServices(database, http, repository, coordinator, vault, new SqliteAppSettingsStore(database));
        }
        catch
        {
            http.Dispose();
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        await database.DisposeAsync();
    }
}
