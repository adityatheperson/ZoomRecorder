using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.ViewModels.Library;

public interface IAppSettingsStore
{
    Task<bool> GetDeleteVideoDefaultAsync(CancellationToken cancellationToken);
    Task SetDeleteVideoDefaultAsync(bool value, CancellationToken cancellationToken);
}

public sealed class SettingsViewModel : LibraryViewModelBase
{
    private readonly ICredentialVault vault;
    private readonly IAppSettingsStore settings;
    private bool deleteVideoByDefault;

    public SettingsViewModel(ICredentialVault vault, IAppSettingsStore settings)
    {
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string ApiKey { get; set; } = string.Empty;
    public bool DeleteVideoByDefault
    {
        get => deleteVideoByDefault;
        set { if (deleteVideoByDefault != value) { deleteVideoByDefault = value; RaisePropertyChanged(); } }
    }
    public string PrivacyText => "Recordings and study files stay local. Only audio or an edited transcript is sent to OpenAI when you approve cloud processing.";

    public async Task LoadAsync(CancellationToken cancellationToken) =>
        DeleteVideoByDefault = await settings.GetDeleteVideoDefaultAsync(cancellationToken);
    public Task SaveKeyAsync(CancellationToken cancellationToken) => vault.SaveApiKeyAsync(ApiKey, cancellationToken);
    public Task DeleteKeyAsync(CancellationToken cancellationToken) => vault.DeleteApiKeyAsync(cancellationToken);
    public Task SavePreferencesAsync(CancellationToken cancellationToken) =>
        settings.SetDeleteVideoDefaultAsync(DeleteVideoByDefault, cancellationToken);
    public async Task<bool> TestKeyAsync(CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(await vault.GetApiKeyAsync(cancellationToken));
}
