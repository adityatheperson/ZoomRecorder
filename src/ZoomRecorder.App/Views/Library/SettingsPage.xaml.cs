using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel viewModel;
    private readonly Action<bool> applyNightMode;
    private bool preferencesLoaded;

    public SettingsPage(SettingsViewModel viewModel, Action<bool> applyNightMode)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.applyNightMode = applyNightMode ?? throw new ArgumentNullException(nameof(applyNightMode));
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.LoadAsync(CancellationToken.None);
            preferencesLoaded = true;
        };
    }

    private async void NightModeToggled(object sender, RoutedEventArgs args)
    {
        if (!preferencesLoaded)
        {
            return;
        }

        applyNightMode(viewModel.NightModeEnabled);
        await viewModel.SaveNightModeAsync(CancellationToken.None);
        ShowStatus(viewModel.NightModeEnabled ? "Night mode enabled." : "Day mode enabled.", InfoBarSeverity.Success);
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
