using Microsoft.UI.Xaml;
using ZoomRecorder.App.Data;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.App.Views;
using ZoomRecorder.App.Views.Library;
using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Meetings;
using ZoomRecorder.App.Interop;
using ZoomRecorder.App.Services;
using WinRT.Interop;
using ZoomRecorder.Core.Ports;
using Microsoft.UI.Windowing;

namespace ZoomRecorder.App;

public sealed partial class MainWindow : Window, IAppNavigator
{
    private readonly NativeSession _nativeSession;
    private readonly NativeJoinFlow _joinFlow;
    private readonly Task<LibraryContext?> _libraryInitialization;

    private const string LibraryUnavailableMessage =
        "Your recording was saved, but the class library is unavailable right now.";

    public MainWindow()
    {
        InitializeComponent();
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        AppWindow.GetFromWindowId(windowId).SetIcon(WindowIconPath.Resolve(AppContext.BaseDirectory));
        _nativeSession = new NativeSession();
        _joinFlow = new NativeJoinFlow(_nativeSession);
        _libraryInitialization = InitializeLibraryAsync();
        _joinFlow.RecordingCompleted += (_, result) => _ = HandleRecordingCompletedAsync(result);
        _joinFlow.FinalizationFailed += (_, message) => DispatcherQueue.TryEnqueue(() =>
        {
            if (RootFrame.Content is MeetingPage meeting) meeting.ShowSaveError(message);
        });
        RootFrame.Content = new JoinPage(new JoinViewModel(_joinFlow, this));
        Closed += OnClosed;
    }

    public void ShowMeeting() => RootFrame.Content = new MeetingPage(_joinFlow.StopAndSaveAsync);

    private async Task HandleRecordingCompletedAsync(RecordingResult result)
    {
        var library = await _libraryInitialization;
        RecordingRecord? recording = null;
        string? assignmentStatus = null;

        if (library is null)
        {
            assignmentStatus = LibraryUnavailableMessage;
        }
        else
        {
            try
            {
                recording = await library.Registration.RegisterFinalizedAsync(
                    result,
                    _joinFlow.CurrentMeetingId,
                    CancellationToken.None);
            }
            catch
            {
                assignmentStatus = LibraryUnavailableMessage;
            }
        }

        DispatcherQueue.TryEnqueue(() => ShowCompletion(result, recording, library, assignmentStatus));
    }

    private void ShowCompletion(
        RecordingResult result,
        RecordingRecord? recording,
        LibraryContext? library,
        string? assignmentStatus)
    {
        var viewModel = new CompletionViewModel(result, recording?.Id, assignmentStatus);
        Func<Task>? assign = recording is not null && library is not null
            ? () => ShowAssignmentDialogAsync(viewModel, library.Repository, recording)
            : null;

        RootFrame.Content = new CompletionPage(
            viewModel,
            () => RootFrame.Content = new JoinPage(new JoinViewModel(_joinFlow, this)),
            assign);
    }

    private async Task ShowAssignmentDialogAsync(
        CompletionViewModel completion,
        ILibraryRepository repository,
        RecordingRecord recording)
    {
        var assignment = new AssignRecordingViewModel(repository, recording);
        try
        {
            await assignment.LoadClassesAsync(CancellationToken.None);
        }
        catch
        {
            completion.MarkAssignmentUnavailable();
            return;
        }

        var dialog = new AssignRecordingDialog(assignment)
        {
            XamlRoot = RootFrame.XamlRoot
        };
        await dialog.ShowAsync();
        if (dialog.AssignedClass is not null)
        {
            completion.MarkAssigned(dialog.AssignedClass.Name);
        }
    }

    private static async Task<LibraryContext?> InitializeLibraryAsync()
    {
        try
        {
            var database = await LibraryDatabase.OpenAsync(
                LibraryPaths.CreateDefault().DatabasePath,
                CancellationToken.None);
            var repository = new SqliteLibraryRepository(database);
            return new LibraryContext(
                database,
                repository,
                new RecordingLibraryService(repository, () => DateTimeOffset.UtcNow));
        }
        catch
        {
            return null;
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _nativeSession.Dispose();
        var library = await _libraryInitialization;
        if (library is not null)
        {
            await library.Database.DisposeAsync();
        }
    }

    private sealed record LibraryContext(
        LibraryDatabase Database,
        ILibraryRepository Repository,
        RecordingLibraryService Registration);
}
