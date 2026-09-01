namespace ZoomRecorder.App.ViewModels.Library;

public interface IAppSettingsStore
{
    Task<bool> GetDeleteVideoDefaultAsync(CancellationToken cancellationToken);
    Task SetDeleteVideoDefaultAsync(bool value, CancellationToken cancellationToken);
}

public sealed class SettingsViewModel : LibraryViewModelBase
{
    private readonly IAppSettingsStore settings;
    private bool deleteVideoByDefault;

    public SettingsViewModel(IAppSettingsStore settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public bool DeleteVideoByDefault
    {
        get => deleteVideoByDefault;
        set { if (deleteVideoByDefault != value) { deleteVideoByDefault = value; RaisePropertyChanged(); } }
    }
    public string PrivacyText => "Recordings and study files stay local. Only audio or an edited transcript is sent to OpenAI when you approve cloud processing.";

    public async Task LoadAsync(CancellationToken cancellationToken) =>
        DeleteVideoByDefault = await settings.GetDeleteVideoDefaultAsync(cancellationToken);
    public Task SavePreferencesAsync(CancellationToken cancellationToken) =>
        settings.SetDeleteVideoDefaultAsync(DeleteVideoByDefault, cancellationToken);
}
