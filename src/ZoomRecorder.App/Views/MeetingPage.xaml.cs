using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ZoomRecorder.App.Views;

public sealed partial class MeetingPage : Page
{
    private readonly Func<Task> stopAndSave;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    public MeetingPage(Func<Task> stopAndSave)
    {
        this.stopAndSave = stopAndSave;
        InitializeComponent();
        timer.Tick += (_, _) => UpdateTimer();
        Loaded += (_, _) => { UpdateTimer(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    private async void StopSaveButton_Click(object sender, RoutedEventArgs e)
    {
        StopSaveButton.IsEnabled = false;
        StopSaveButton.Content = "Saving…";
        try
        {
            await stopAndSave();
        }
        catch (Exception exception)
        {
            ShowSaveError(exception.Message);
            StopSaveButton.Content = "Try Stop & Save Again";
            StopSaveButton.IsEnabled = true;
        }
    }

    public void ShowSaveError(string message)
    {
        SaveErrorText.Text = message;
        SaveErrorText.Visibility = Visibility.Visible;
    }

    private void UpdateTimer()
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        RecordingTimerText.Text = $"Recording · {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
