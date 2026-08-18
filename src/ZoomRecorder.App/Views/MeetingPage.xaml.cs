using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ZoomRecorder.App.Views;

public sealed partial class MeetingPage : Page
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    public MeetingPage()
    {
        InitializeComponent();
        timer.Tick += (_, _) => UpdateTimer();
        Loaded += (_, _) => { UpdateTimer(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    private void UpdateTimer()
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        RecordingTimerText.Text = $"Recording · {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
