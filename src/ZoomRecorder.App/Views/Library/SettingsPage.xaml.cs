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

    private async void SavePreferencesClicked(object sender, RoutedEventArgs args)
    {
        await viewModel.SavePreferencesAsync(CancellationToken.None);
        ShowStatus("Preferences saved.", InfoBarSeverity.Success);
    }
    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        PreferencesStatus.Message = message;
        PreferencesStatus.Severity = severity;
        PreferencesStatus.IsOpen = true;
    }
}
