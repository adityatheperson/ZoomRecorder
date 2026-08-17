using Microsoft.UI.Xaml;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.Views;
using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.App;

public sealed partial class MainWindow : Window, IAppNavigator
{
    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        RootFrame.Content = new JoinPage(new JoinViewModel(new SimulatedJoinFlow(), this));
#else
        RootFrame.Content = new JoinPage(new JoinViewModel(new UnavailableReleaseFlow(), this));
#endif
    }

    public void ShowMeeting() => RootFrame.Content = new MeetingPage();

    private sealed class SimulatedJoinFlow : IJoinFlow
    {
        public async Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken) =>
            await Task.Delay(500, cancellationToken);
    }

    private sealed class UnavailableReleaseFlow : IJoinFlow
    {
        public Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("The native Zoom adapter is not included in this build."));
    }
}
