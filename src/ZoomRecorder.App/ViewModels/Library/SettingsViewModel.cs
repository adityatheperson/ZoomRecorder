using ZoomRecorder.App.Data;

namespace ZoomRecorder.App.ViewModels.Library;

public interface IAppSettingsStore
{
    Task<bool> GetDeleteVideoDefaultAsync(CancellationToken cancellationToken);
    Task SetDeleteVideoDefaultAsync(bool value, CancellationToken cancellationToken);
    Task<bool> GetNightModeAsync(CancellationToken cancellationToken);
    Task SetNightModeAsync(bool value, CancellationToken cancellationToken);
}

public sealed class SettingsViewModel : LibraryViewModelBase
{
    private readonly IAppSettingsStore settings;
    private readonly IStorageLocationSettingsStore storageLocations;
    private bool deleteVideoByDefault;
    private bool nightModeEnabled;
    private string recordingsDirectory = string.Empty;
    private string transcriptsDirectory = string.Empty;

    public SettingsViewModel(IAppSettingsStore settings, IStorageLocationSettingsStore? storageLocations = null)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.storageLocations = storageLocations ?? StorageLocationSettingsStore.CreateDefault();
    }

    public bool DeleteVideoByDefault
    {
        get => deleteVideoByDefault;
        set { if (deleteVideoByDefault != value) { deleteVideoByDefault = value; RaisePropertyChanged(); } }
    }
    public bool NightModeEnabled
    {
        get => nightModeEnabled;
        set { if (nightModeEnabled != value) { nightModeEnabled = value; RaisePropertyChanged(); } }
    }
    public string RecordingsDirectory
    {
        get => recordingsDirectory;
        set { if (recordingsDirectory != value) { recordingsDirectory = value; RaisePropertyChanged(); } }
    }
    public string TranscriptsDirectory
    {
        get => transcriptsDirectory;
        set { if (transcriptsDirectory != value) { transcriptsDirectory = value; RaisePropertyChanged(); } }
    }
    public string PrivacyText => "Recordings and study files stay local. Only audio or an edited transcript is sent to OpenAI when you approve cloud processing.";

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        DeleteVideoByDefault = await settings.GetDeleteVideoDefaultAsync(cancellationToken);
        NightModeEnabled = await settings.GetNightModeAsync(cancellationToken);
        var locations = await storageLocations.LoadAsync(cancellationToken);
        RecordingsDirectory = locations.RecordingsDirectory;
        TranscriptsDirectory = locations.TranscriptsDirectory;
    }
    public Task SavePreferencesAsync(CancellationToken cancellationToken) =>
        settings.SetDeleteVideoDefaultAsync(DeleteVideoByDefault, cancellationToken);
    public Task SaveNightModeAsync(CancellationToken cancellationToken) =>
        settings.SetNightModeAsync(NightModeEnabled, cancellationToken);
    public Task SaveStorageLocationsAsync(CancellationToken cancellationToken) =>
        storageLocations.SaveAsync(
            new StorageLocationSettings(RecordingsDirectory, TranscriptsDirectory),
            cancellationToken);
}
