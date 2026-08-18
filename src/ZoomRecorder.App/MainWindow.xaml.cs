using Microsoft.UI.Xaml;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.Views;
using ZoomRecorder.Core.Meetings;
using ZoomRecorder.App.Interop;
using ZoomRecorder.App.Services;
using WinRT.Interop;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App;

public sealed partial class MainWindow : Window, IAppNavigator
{
    private readonly NativeSession _nativeSession;
    private readonly NativeJoinFlow _joinFlow;

    public MainWindow()
    {
        InitializeComponent();
        _nativeSession = new NativeSession();
        _joinFlow = new NativeJoinFlow(_nativeSession);
        _joinFlow.RecordingCompleted += (_, result) => DispatcherQueue.TryEnqueue(() => ShowCompletion(result));
        _joinFlow.FinalizationFailed += (_, message) => DispatcherQueue.TryEnqueue(() =>
        {
            if (RootFrame.Content is MeetingPage meeting) meeting.ShowSaveError(message);
        });
        RootFrame.Content = new JoinPage(new JoinViewModel(_joinFlow, this));
        Closed += (_, _) => _nativeSession.Dispose();
    }

    public void ShowMeeting() => RootFrame.Content = new MeetingPage(_joinFlow.StopAndSaveAsync);

    private void ShowCompletion(RecordingResult result)
    {
        RootFrame.Content = new CompletionPage(new CompletionViewModel(result), () => RootFrame.Content = new JoinPage(new JoinViewModel(_joinFlow, this)));
    }
}
