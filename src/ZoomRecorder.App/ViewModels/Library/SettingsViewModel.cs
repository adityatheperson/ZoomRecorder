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
    private bool deleteVideoByDefault;
    private bool nightModeEnabled;

    public SettingsViewModel(IAppSettingsStore settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
    public string PrivacyText => "Recordings and study files stay local. Only audio or an edited transcript is sent to OpenAI when you approve cloud processing.";

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        DeleteVideoByDefault = await settings.GetDeleteVideoDefaultAsync(cancellationToken);
        NightModeEnabled = await settings.GetNightModeAsync(cancellationToken);
    }
    public Task SavePreferencesAsync(CancellationToken cancellationToken) =>
        settings.SetDeleteVideoDefaultAsync(DeleteVideoByDefault, cancellationToken);
    public Task SaveNightModeAsync(CancellationToken cancellationToken) =>
        settings.SetNightModeAsync(NightModeEnabled, cancellationToken);
}
