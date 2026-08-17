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
    private NativeHostWindow? _meetingHost;
    private readonly NativeJoinFlow _joinFlow;

    public MainWindow()
    {
        InitializeComponent();
        _nativeSession = new NativeSession();
        _joinFlow = new NativeJoinFlow(_nativeSession, EnsureMeetingHost);
        _joinFlow.RecordingCompleted += (_, result) => DispatcherQueue.TryEnqueue(() => ShowCompletion(result));
        RootFrame.Content = new JoinPage(new JoinViewModel(_joinFlow, this));
        Closed += (_, _) => { _meetingHost?.Dispose(); _nativeSession.Dispose(); };
    }

    public void ShowMeeting() => RootFrame.Content = new MeetingPage();

    private nint EnsureMeetingHost()
    {
        if (_meetingHost is not null) return _meetingHost.Handle;
        var windowHandle = WindowNative.GetWindowHandle(this);
        _meetingHost = new NativeHostWindow(windowHandle, 1200, 680);
        return _meetingHost.Handle;
    }

    private void ShowCompletion(RecordingResult result)
    {
        _meetingHost?.Dispose();
        _meetingHost = null;
        RootFrame.Content = new CompletionPage(new CompletionViewModel(result), () => RootFrame.Content = new JoinPage(new JoinViewModel(_joinFlow, this)));
    }
}
