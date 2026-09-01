using ZoomRecorder.App.Data;

namespace ZoomRecorder.App.Tests.Data;

public sealed class StorageLocationSettingsStoreTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), $"ZoomRecorder.StorageTests.{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_WhenSettingsAreMissing_ReturnsDefaults()
    {
        var store = CreateStore();

        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(Path.GetFullPath(Path.Combine(_testRoot, "default-recordings")), settings.RecordingsDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(_testRoot, "default-transcripts")), settings.TranscriptsDirectory);
    }

    [Fact]
    public async Task SaveAsync_PersistsNormalizedLocationsAndCreatesDirectories()
    {
        var store = CreateStore();
        var recordings = Path.Combine(_testRoot, "custom", "recordings");
        var transcripts = Path.Combine(_testRoot, "custom", "transcripts");

        await store.SaveAsync(new StorageLocationSettings(recordings, transcripts), CancellationToken.None);
        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(Path.GetFullPath(recordings), settings.RecordingsDirectory);
        Assert.Equal(Path.GetFullPath(transcripts), settings.TranscriptsDirectory);
        Assert.True(Directory.Exists(recordings));
        Assert.True(Directory.Exists(transcripts));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private StorageLocationSettingsStore CreateStore()
    {
        return new StorageLocationSettingsStore(
            Path.Combine(_testRoot, "settings.json"),
            Path.Combine(_testRoot, "default-recordings"),
            Path.Combine(_testRoot, "default-transcripts"));
    }
}
