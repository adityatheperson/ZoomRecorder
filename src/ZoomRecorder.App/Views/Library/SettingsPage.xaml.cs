using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync(CancellationToken.None);
    }

    private async void SaveKeyClicked(object sender, RoutedEventArgs args)
    {
        viewModel.ApiKey = ApiKeyBox.Password;
        await viewModel.SaveKeyAsync(CancellationToken.None);
        ShowStatus("API key saved.", InfoBarSeverity.Success);
    }
    private async void TestKeyClicked(object sender, RoutedEventArgs args) =>
        ShowStatus(await viewModel.TestKeyAsync(CancellationToken.None) ? "An API key is stored." : "No API key is stored.", InfoBarSeverity.Informational);
    private async void DeleteKeyClicked(object sender, RoutedEventArgs args)
    {
        await viewModel.DeleteKeyAsync(CancellationToken.None);
        ApiKeyBox.Password = string.Empty;
        ShowStatus("API key deleted.", InfoBarSeverity.Success);
    }
    private async void SavePreferencesClicked(object sender, RoutedEventArgs args)
    {
        await viewModel.SavePreferencesAsync(CancellationToken.None);
        ShowStatus("Preferences saved.", InfoBarSeverity.Success);
    }
    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        KeyStatus.Message = message;
        KeyStatus.Severity = severity;
        KeyStatus.IsOpen = true;
    }
}
