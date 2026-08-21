using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using ZoomRecorder.App.Data;
using ZoomRecorder.App.Composition;
using ZoomRecorder.App.Interop;
using ZoomRecorder.App.Services;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.App.Views;
using ZoomRecorder.App.Views.Library;
using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App;

public sealed partial class MainWindow : Window, IAppNavigator
{
    private readonly NativeSession _nativeSession;
    private readonly NativeJoinFlow _joinFlow;
    private Task<LibraryContext?> _libraryInitialization;
    private LibraryShellViewModel? _shellViewModel;
    private int _navigationRequest;
    private bool _classDetailActive;
    private bool _suppressNavigationSelection;

    private const string LibraryUnavailableMessage =
        "Your recording was saved, but the class library is unavailable right now.";

    private readonly AppServices _services;

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services ?? throw new ArgumentNullException(nameof(services));
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        AppWindow.GetFromWindowId(windowId).SetIcon(WindowIconPath.Resolve(AppContext.BaseDirectory));
        _nativeSession = new NativeSession();
        _joinFlow = new NativeJoinFlow(_nativeSession);
        _libraryInitialization = Task.FromResult<LibraryContext?>(CreateLibraryContext(services));
        _joinFlow.RecordingCompleted += (_, result) => _ = HandleRecordingCompletedAsync(result);
        _joinFlow.FinalizationFailed += (_, message) => DispatcherQueue.TryEnqueue(() =>
        {
            if (RootFrame.Content is MeetingPage meeting) meeting.ShowSaveError(message);
        });
        Closed += OnClosed;
        NavigateHome();
    }

    public void ShowMeeting()
    {
        CancelPendingNavigation();
        _shellViewModel?.Navigate(LibraryDestination.Meeting);
        SetNavigationVisible(false);
        RootFrame.Content = new MeetingPage(_joinFlow.StopAndSaveAsync);
    }

    private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigationSelection)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            NavigateSettings();
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "home":
                NavigateHome();
                break;
            case "classes":
                NavigateClasses();
                break;
            case "recordings":
                NavigateRecordings();
                break;
        }
    }

    private void NavigationBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (_classDetailActive)
        {
            NavigateClasses();
        }
    }

    private void RecordClassClicked(object sender, RoutedEventArgs args) => ShowJoin();

    private void NavigateHome()
    {
        var request = BeginLibraryNavigation(LibraryDestination.Home, HomeNavigationItem);
        RootFrame.Content = new HomePage(
            viewModel: null,
            ShowJoin,
            NavigateRecordings,
            RetryHomeAsync,
            isLoading: true);
        _ = LoadHomeAsync(request);
    }

    private async Task LoadHomeAsync(int request)
    {
        var library = await _libraryInitialization;
        if (request != _navigationRequest)
        {
            return;
        }

        if (library is null)
        {
            RootFrame.Content = new HomePage(null, ShowJoin, NavigateRecordings, RetryHomeAsync);
            return;
        }

        var viewModel = GetShellViewModel(library.Repository);
        await viewModel.LoadAsync(CancellationToken.None);
        if (request == _navigationRequest)
        {
            RootFrame.Content = new HomePage(viewModel, ShowJoin, NavigateRecordings, RetryHomeAsync);
        }
    }

    private void NavigateClasses()
    {
        var request = BeginLibraryNavigation(LibraryDestination.Classes, ClassesNavigationItem);
        RootFrame.Content = new ClassesPage(
            viewModel: null,
            OpenClass,
            ShowJoin,
            NavigateRecordings,
            RetryClassesAsync,
            isLoading: true);
        _ = LoadClassesAsync(request);
    }

    private async Task LoadClassesAsync(int request)
    {
        var library = await _libraryInitialization;
        if (request != _navigationRequest)
        {
            return;
        }

        if (library is null)
        {
            RootFrame.Content = new ClassesPage(null, OpenClass, ShowJoin, NavigateRecordings, RetryClassesAsync);
            return;
        }

        var viewModel = new ClassesViewModel(library.Repository);
        await viewModel.LoadAsync(CancellationToken.None);
        if (request == _navigationRequest)
        {
            RootFrame.Content = new ClassesPage(viewModel, OpenClass, ShowJoin, NavigateRecordings, RetryClassesAsync);
        }
    }

    private void NavigateRecordings()
    {
        var request = BeginLibraryNavigation(LibraryDestination.Recordings, RecordingsNavigationItem);
        RootFrame.Content = new RecordingsPage(
            viewModel: null,
            assignment: null,
            ShowJoin,
            RetryRecordingsAsync,
            isLoading: true);
        _ = LoadRecordingsAsync(request);
    }

    private async Task LoadRecordingsAsync(int request)
    {
        var library = await _libraryInitialization;
        if (request != _navigationRequest)
        {
            return;
        }

        if (library is null)
        {
            RootFrame.Content = new RecordingsPage(null, null, ShowJoin, RetryRecordingsAsync);
            return;
        }

        var viewModel = new RecordingsViewModel(library.Repository);
        await viewModel.LoadAsync(CancellationToken.None);
        if (request == _navigationRequest)
        {
            RootFrame.Content = new RecordingsPage(
                viewModel,
                (recording, cancellationToken) => ShowAssignmentDialogAsync(
                    library.Repository,
                    recording,
                    cancellationToken),
                ShowJoin,
                RetryRecordingsAsync);
        }
    }

    private void OpenClass(ClassCardViewModel classCard)
    {
        var request = BeginLibraryNavigation(LibraryDestination.Classes, ClassesNavigationItem);
        _classDetailActive = true;
        NavigationRoot.IsBackEnabled = true;
        _ = LoadClassDetailAsync(request, classCard.Id);
    }

    private async Task LoadClassDetailAsync(int request, Guid classId)
    {
        var library = await _libraryInitialization;
        if (library is null || request != _navigationRequest)
        {
            return;
        }

        var viewModel = new ClassDetailViewModel(library.Repository, classId);
        await viewModel.LoadAsync(CancellationToken.None);
        if (request == _navigationRequest)
        {
            RootFrame.Content = new ClassDetailPage(viewModel, NavigateClasses, ShowJoin);
        }
    }

    private void NavigateSettings()
    {
        BeginLibraryNavigation(LibraryDestination.Settings, navigationItem: null);
        RootFrame.Content = new SettingsPage(new SettingsViewModel(_services.CredentialVault, _services.Settings));
    }

    private void ShowJoin()
    {
        BeginLibraryNavigation(LibraryDestination.RecordClass, navigationItem: null);
        RootFrame.Content = new JoinPage(new JoinViewModel(_joinFlow, this));
    }

    private int BeginLibraryNavigation(LibraryDestination destination, NavigationViewItem? navigationItem)
    {
        var request = ++_navigationRequest;
        _classDetailActive = false;
        _shellViewModel?.Navigate(destination);
        SetNavigationVisible(true);
        NavigationRoot.IsBackEnabled = false;
        if (navigationItem is not null)
        {
            _suppressNavigationSelection = true;
            NavigationRoot.SelectedItem = navigationItem;
            _suppressNavigationSelection = false;
        }

        return request;
    }

    private void CancelPendingNavigation() => ++_navigationRequest;

    private void SetNavigationVisible(bool visible)
    {
        NavigationRoot.IsPaneVisible = visible;
        NavigationRoot.IsPaneToggleButtonVisible = visible;
        NavigationRoot.IsBackButtonVisible = visible
            ? NavigationViewBackButtonVisible.Visible
            : NavigationViewBackButtonVisible.Collapsed;
        if (visible)
        {
            _shellViewModel?.Navigate(_shellViewModel.CurrentDestination == LibraryDestination.Meeting
                ? LibraryDestination.Home
                : _shellViewModel.CurrentDestination);
        }
    }

    private LibraryShellViewModel GetShellViewModel(ILibraryRepository repository) =>
        _shellViewModel ??= new LibraryShellViewModel(repository);

    private async Task RetryHomeAsync()
    {
        await RestartLibraryInitializationIfUnavailableAsync();
        NavigateHome();
    }

    private async Task RetryClassesAsync()
    {
        await RestartLibraryInitializationIfUnavailableAsync();
        NavigateClasses();
    }

    private async Task RetryRecordingsAsync()
    {
        await RestartLibraryInitializationIfUnavailableAsync();
        NavigateRecordings();
    }

    private async Task RestartLibraryInitializationIfUnavailableAsync()
    {
        if (await _libraryInitialization is null)
        {
            _shellViewModel = null;
            _libraryInitialization = Task.FromResult<LibraryContext?>(CreateLibraryContext(_services));
        }
    }

    private static LibraryContext CreateLibraryContext(AppServices services) => new(
        services.Database,
        services.Repository,
        new RecordingLibraryService(services.Repository, () => DateTimeOffset.UtcNow));

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
        CancelPendingNavigation();
        SetNavigationVisible(true);
        var viewModel = new CompletionViewModel(result, recording?.Id, assignmentStatus);
        Func<Task>? assign = recording is not null && library is not null
            ? () => ShowCompletionAssignmentDialogAsync(viewModel, library.Repository, recording)
            : null;

        RootFrame.Content = new CompletionPage(viewModel, ShowJoin, assign);
    }

    private async Task ShowCompletionAssignmentDialogAsync(
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

    private async Task<bool> ShowAssignmentDialogAsync(
        ILibraryRepository repository,
        RecordingRecord recording,
        CancellationToken cancellationToken)
    {
        var assignment = new AssignRecordingViewModel(repository, recording);
        await assignment.LoadClassesAsync(cancellationToken);

        var dialog = new AssignRecordingDialog(assignment)
        {
            XamlRoot = RootFrame.XamlRoot
        };
        await dialog.ShowAsync();
        return dialog.AssignedClass is not null;
    }

    private void OnClosed(object sender, WindowEventArgs args) => _nativeSession.Dispose();

    private sealed record LibraryContext(
        LibraryDatabase Database,
        ILibraryRepository Repository,
        RecordingLibraryService Registration);
}
