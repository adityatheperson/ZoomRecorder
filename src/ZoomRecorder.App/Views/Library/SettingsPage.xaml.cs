using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZoomRecorder.App.ViewModels.Library;

namespace ZoomRecorder.App.Views.Library;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel viewModel;
    private readonly Action<bool> applyNightMode;
    private readonly Func<Task<string?>> pickFolder;
    private bool preferencesLoaded;

    public SettingsPage(
        SettingsViewModel viewModel,
        Action<bool> applyNightMode,
        Func<Task<string?>> pickFolder)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.applyNightMode = applyNightMode ?? throw new ArgumentNullException(nameof(applyNightMode));
        this.pickFolder = pickFolder ?? throw new ArgumentNullException(nameof(pickFolder));
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.LoadAsync(CancellationToken.None);
            preferencesLoaded = true;
        };
    }

    private async void BrowseRecordingsClicked(object sender, RoutedEventArgs args)
    {
        if (await pickFolder() is { } path)
        {
            viewModel.RecordingsDirectory = path;
        }
    }

    private async void BrowseTranscriptsClicked(object sender, RoutedEventArgs args)
    {
        if (await pickFolder() is { } path)
        {
            viewModel.TranscriptsDirectory = path;
        }
    }

    private async void SaveStorageLocationsClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            await viewModel.SaveStorageLocationsAsync(CancellationToken.None);
            ShowStatus("Storage locations saved. Restart Zoom Recorder to use them.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Storage locations could not be saved: {exception.Message}", InfoBarSeverity.Error);
        }
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
