using ZoomRecorder.App.Data;
using ZoomRecorder.App.LocalTranscription;
using ZoomRecorder.App.Media;
using ZoomRecorder.App.Security;
using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Composition;

public sealed class AppServices : IAsyncDisposable
{
    private readonly LibraryDatabase database;
    private readonly HttpClient httpClient;
    private readonly Dictionary<Guid, ProcessingJobSnapshot> resumableByRecording;

    private AppServices(
        LibraryDatabase database,
        HttpClient httpClient,
        SqliteLibraryRepository repository,
        SqliteProcessingJobStore jobs,
        ProcessingCoordinator coordinator,
        ICredentialVault credentialVault,
        IAppSettingsStore settings,
        IProcessingArtifactStore artifacts,
        StudyMaterialMergeService studyMaterials,
        LibraryPaths paths,
        IReadOnlyList<ProcessingJobSnapshot> recoveredJobs)
    {
        this.database = database;
        this.httpClient = httpClient;
        Repository = repository;
        Jobs = jobs;
        Coordinator = coordinator;
        CredentialVault = credentialVault;
        Settings = settings;
        Artifacts = artifacts;
        StudyMaterials = studyMaterials;
        Paths = paths;
        resumableByRecording = recoveredJobs
            .GroupBy(job => job.Request.RecordingId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(job => job.UpdatedAt).First());
    }

    public SqliteLibraryRepository Repository { get; }
    public SqliteProcessingJobStore Jobs { get; }
    public ProcessingCoordinator Coordinator { get; }
    public ICredentialVault CredentialVault { get; }
    public IAppSettingsStore Settings { get; }
    public IProcessingArtifactStore Artifacts { get; }
    public StudyMaterialMergeService StudyMaterials { get; }
    public LibraryPaths Paths { get; }
    public LibraryDatabase Database => database;

    public ProcessingJobSnapshot? FindResumableJob(Guid recordingId) =>
        resumableByRecording.GetValueOrDefault(recordingId);

    public async Task RefreshTrackedJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await Jobs.LoadAsync(jobId, cancellationToken);
        if (job.State is ProcessingState.Completed or ProcessingState.Cancelled)
        {
            resumableByRecording.Remove(job.Request.RecordingId);
        }
        else
        {
            resumableByRecording[job.Request.RecordingId] = job;
        }
    }

    public async Task<string> GetRecordingProcessingStatusAsync(
        Guid recordingId,
        CancellationToken cancellationToken)
    {
        if (FindResumableJob(recordingId) is { } resumable)
        {
            return resumable.State == ProcessingState.NeedsAttention
                ? "Needs attention"
                : "Resume transcription";
        }

        try
        {
            var checkpoint = await Repository.GetTranscriptAsync(recordingId, cancellationToken);
            return await Artifacts.ReadVerifiedAsync(checkpoint, cancellationToken) is null
                ? "Needs attention"
                : "Transcript ready";
        }
        catch (KeyNotFoundException)
        {
            return "Not transcribed";
        }
    }

    public static async Task<AppServices> CreateAsync(
        LibraryPaths paths,
        CancellationToken cancellationToken)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        try
        {
            return await CreateAsync(
                paths,
                http,
                new WindowsCredentialVault(),
                LoadDefaultManifest(),
                LocalTranscriptionPaths.CreateDefault(),
                cancellationToken);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    internal static async Task<AppServices> CreateAsync(
        LibraryPaths paths,
        HttpClient http,
        ICredentialVault credentialVault,
        WhisperModelManifest manifest,
        LocalTranscriptionPaths localPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentialVault);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(localPaths);
        Directory.CreateDirectory(paths.ArtifactsRoot);
        Directory.CreateDirectory(paths.JobsRoot);
        var database = await LibraryDatabase.OpenAsync(paths.DatabasePath, cancellationToken);
        try
        {
            var repository = new SqliteLibraryRepository(database);
            var jobs = new SqliteProcessingJobStore(database);
            var artifacts = new ArtifactStore(paths.ArtifactsRoot);
            var disabledStudyGeneration = new DisabledStudyGenerationClient();
            var transcription = new LocalWhisperTranscriptionClient(
                new WhisperModelManager(http, manifest, localPaths.ModelsRoot),
                new NativeLocalPcmAudioConverter(),
                new WhisperWorkerRunner(localPaths.GpuWorkerPath, localPaths.CpuWorkerPath));
            var coordinator = new ProcessingCoordinator(
                jobs,
                new NativeAudioChunkPreparer(),
                transcription,
                disabledStudyGeneration,
                artifacts,
                NativeAudioChunkPreparer.DefaultMaxBytes);
            var recoveredJobs = await coordinator.RecoverAsync(cancellationToken);
            return new AppServices(
                database,
                http,
                repository,
                jobs,
                coordinator,
                credentialVault,
                new SqliteAppSettingsStore(database),
                artifacts,
                new StudyMaterialMergeService(repository, disabledStudyGeneration, artifacts),
                paths,
                recoveredJobs);
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static WhisperModelManifest LoadDefaultManifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Whisper", "model-small.en.json");
        using var stream = File.OpenRead(path);
        return WhisperModelManifest.Load(stream);
    }

    public async ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        await database.DisposeAsync();
    }

    private sealed class DisabledStudyGenerationClient : IStudyGenerationClient
    {
        public Task<StudyPackage> GenerateLectureAsync(Transcript transcript, CancellationToken cancellationToken) =>
            Task.FromException<StudyPackage>(new NotSupportedException("Cloud study-material generation is unavailable in local transcription mode."));

        public Task<ClassStudyGuide> GenerateGuideAsync(
            IReadOnlyList<StudyPackage> lectures,
            CancellationToken cancellationToken) =>
            Task.FromException<ClassStudyGuide>(new NotSupportedException("Cloud class-guide generation is unavailable in local transcription mode."));
    }
}
