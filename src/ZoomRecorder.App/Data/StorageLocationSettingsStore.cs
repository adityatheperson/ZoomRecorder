using System.Text.Json;

namespace ZoomRecorder.App.Data;

public sealed record StorageLocationSettings(string RecordingsDirectory, string TranscriptsDirectory);

public interface IStorageLocationSettingsStore
{
    Task<StorageLocationSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(StorageLocationSettings settings, CancellationToken cancellationToken);
}

public sealed class StorageLocationSettingsStore(
    string settingsPath,
    string? defaultRecordingsDirectory = null,
    string? defaultTranscriptsDirectory = null) : IStorageLocationSettingsStore
{
    private readonly string settingsPath = Path.GetFullPath(settingsPath ?? throw new ArgumentNullException(nameof(settingsPath)));
    private readonly StorageLocationSettings defaultLocations = new(
        Path.GetFullPath(defaultRecordingsDirectory ?? LibraryPaths.DefaultRecordingsRoot()),
        Path.GetFullPath(defaultTranscriptsDirectory ?? LibraryPaths.DefaultArtifactsRoot()));

    public static StorageLocationSettingsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZoomRecorder",
        "storage-locations.json"));

    public static StorageLocationSettings DefaultLocations() => new(
        LibraryPaths.DefaultRecordingsRoot(),
        LibraryPaths.DefaultArtifactsRoot());

    public async Task<StorageLocationSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(settingsPath);
            var saved = await JsonSerializer.DeserializeAsync<StorageLocationSettings>(stream, cancellationToken: cancellationToken);
            return saved is null ? defaultLocations : Normalize(saved);
        }
        catch (FileNotFoundException)
        {
            return defaultLocations;
        }
        catch (DirectoryNotFoundException)
        {
            return defaultLocations;
        }
        catch (JsonException)
        {
            return defaultLocations;
        }
        catch (ArgumentException)
        {
            return defaultLocations;
        }
    }

    public async Task SaveAsync(StorageLocationSettings settings, CancellationToken cancellationToken)
    {
        var normalized = Normalize(settings ?? throw new ArgumentNullException(nameof(settings)));
        Directory.CreateDirectory(normalized.RecordingsDirectory);
        Directory.CreateDirectory(normalized.TranscriptsDirectory);
        var parent = Path.GetDirectoryName(settingsPath) ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporaryPath = settingsPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, normalized, cancellationToken: cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, settingsPath, overwrite: true);
    }

    private static StorageLocationSettings Normalize(StorageLocationSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.RecordingsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.TranscriptsDirectory);
        return new StorageLocationSettings(
            Path.GetFullPath(settings.RecordingsDirectory),
            Path.GetFullPath(settings.TranscriptsDirectory));
    }
}
