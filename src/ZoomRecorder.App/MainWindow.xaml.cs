using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using ZoomRecorder.App.Data;
using ZoomRecorder.App.Composition;
using ZoomRecorder.App.Deletion;
using ZoomRecorder.App.Renaming;
using ZoomRecorder.App.Interop;
using ZoomRecorder.App.Services;
using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.App.Views;
using ZoomRecorder.App.Views.Library;
using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Meetings;
using ZoomRecorder.Core.Ports;
using ZoomRecorder.Core.Processing;
using System.Text.Json;
using Windows.Storage.Pickers;

namespace ZoomRecorder.App;

public sealed partial class MainWindow : Window, IAppNavigator
{
    private readonly NativeSession _nativeSession;
    private readonly ExternalZoomJoinFlow _joinFlow;
    private Task<LibraryContext?> _libraryInitialization;
    private LibraryShellViewModel? _shellViewModel;
    private int _navigationRequest;
    private bool _classDetailActive;
    private bool _suppressNavigationSelection;

    private const string LibraryUnavailableMessage =
        "Your recording was saved, but the class library is unavailable right now.";

    private readonly AppServices _services;
    private readonly RecordingDeletionService _recordingDeletion;
    private readonly RecordingRenameService _recordingRename;

    public MainWindow(AppServices services, bool nightModeEnabled = false)
    {
        InitializeComponent();
        ApplyNightMode(nightModeEnabled);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _recordingDeletion = new RecordingDeletionService(services.Database, services.Paths);
        _recordingRename = new RecordingRenameService(services.Database, services.Paths);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.SetIcon(WindowIconPath.Resolve(AppContext.BaseDirectory));
        ConfigureTitleBar(appWindow);
        _nativeSession = new NativeSession();
        _joinFlow = new ExternalZoomJoinFlow(_nativeSession, services.Paths.RecordingsRoot);
        _libraryInitialization = Task.FromResult<LibraryContext?>(CreateLibraryContext(services));
        _joinFlow.RecordingCompleted += (_, result) => _ = HandleRecordingCompletedAsync(result);
        _joinFlow.FinalizationFailed += (_, message) => DispatcherQueue.TryEnqueue(() =>
        {
            if (RootFrame.Content is MeetingPage meeting) meeting.ShowSaveError(message);
        });
        Closed += OnClosed;
        NavigateHome();
    }

    private static void ConfigureTitleBar(AppWindow appWindow)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = appWindow.TitleBar;
        titleBar.BackgroundColor = Microsoft.UI.Colors.Black;
        titleBar.ForegroundColor = Microsoft.UI.Colors.White;
        titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Black;
        titleBar.InactiveForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Black;
        titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 38, 38, 38);
        titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 64, 64, 64);
        titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Black;
        titleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 190, 190, 190);
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

        var viewModel = new RecordingsViewModel(
            library.Repository,
            _services.GetRecordingProcessingStatusAsync);
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
                RetryRecordingsAsync,
                deletion: (recording, cancellationToken) =>
                    _recordingDeletion.DeleteAsync(recording.Id, cancellationToken),
                rename: (recording, name, cancellationToken) =>
                    _recordingRename.RenameAsync(recording.Id, name, cancellationToken));
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
            RootFrame.Content = new ClassDetailPage(
                viewModel,
                NavigateClasses,
                ShowJoin,
                OpenLecture,
                (recording, cancellationToken) =>
                    _recordingDeletion.DeleteAsync(recording.Id, cancellationToken),
                (recording, name, cancellationToken) =>
                    _recordingRename.RenameAsync(recording.Id, name, cancellationToken));
        }
    }

    private void OpenLecture(RecordingListItem item)
    {
        var request = ++_navigationRequest;
        _classDetailActive = true;
        NavigationRoot.IsBackEnabled = true;
        _ = LoadLectureAsync(request, item.Recording);
    }

    private async Task LoadLectureAsync(int request, RecordingRecord recording)
    {
        var notice = new DisabledNoticePresenter();
        var viewModel = new LectureDetailViewModel(
            recording,
            (text, token) => SaveEditedTranscriptAsync(recording, text, token),
            _ => Task.CompletedTask,
            notice);
        try
        {
            viewModel.TranscriptText = await LoadTranscriptTextAsync(recording.Id, CancellationToken.None);
        }
        catch (KeyNotFoundException)
        {
            viewModel.TranscriptText = string.Empty;
        }

        if (request == _navigationRequest)
        {
            RootFrame.Content = new LectureDetailPage(
                viewModel,
                () => NavigateClassById(recording.ClassId!.Value),
                () => ShowProcessingAsync(recording, viewModel, notice));
        }
    }

    private void NavigateClassById(Guid classId)
    {
        var request = BeginLibraryNavigation(LibraryDestination.Classes, ClassesNavigationItem);
        _classDetailActive = true;
        NavigationRoot.IsBackEnabled = true;
        _ = LoadClassDetailAsync(request, classId);
    }

    private async Task SaveEditedTranscriptAsync(RecordingRecord recording, string text, CancellationToken token)
    {
        var checkpoint = await _services.Repository.GetTranscriptAsync(recording.Id, token);
        var bytes = await _services.Artifacts.ReadVerifiedAsync(checkpoint, token)
            ?? throw new InvalidDataException("The transcript artifact is unavailable.");
        var original = JsonSerializer.Deserialize<Transcript>(bytes.Span)
            ?? throw new InvalidDataException("The transcript artifact is invalid.");
        var transcript = original with { EditedText = text };
        var artifact = await _services.Artifacts.WriteRecordingArtifactAsync(
            recording.Id, $"transcript-edited-{Guid.NewGuid():D}.json",
            JsonSerializer.SerializeToUtf8Bytes(transcript), token);
        await _services.StudyMaterials.SaveEditedTranscriptAsync(recording.Id, artifact, token);
    }

    private async Task ShowProcessingAsync(
        RecordingRecord recording,
        LectureDetailViewModel lecture,
        ICloudNoticePresenter notice)
    {
        if (recording.ClassId is not { } classId) return;
        var recovered = _services.FindResumableJob(recording.Id);
        var jobId = recovered?.Request.JobId ?? Guid.NewGuid();
        var jobDirectory = recovered?.Request.JobDirectory ??
            Path.Combine(_services.Paths.JobsRoot, jobId.ToString("D"));
        var attempted = false;
        async Task StartOrResumeAsync(bool _, CancellationToken token)
        {
            attempted = true;
            if (recovered is null)
            {
                await _services.Coordinator.StartAsync(new ProcessingRequest(
                    jobId, recording.Id, classId, recording.FilePath, jobDirectory,
                    DeleteVideoAfterSuccess: false), token);
            }
            else
            {
                await _services.Coordinator.ResumeAsync(jobId, token);
            }
        }

        ProcessingViewModel viewModel = null!;
        viewModel = new ProcessingViewModel(
            "Selected class", null, savedDeleteDefault: false, notice,
            StartOrResumeAsync,
            token => _services.Coordinator.CancelAsync(jobId, token),
            resume: token => _services.Coordinator.ResumeAsync(jobId, token));
        EventHandler<ProcessingProgress> progress = (_, update) =>
        {
            if (update.JobId == jobId) DispatcherQueue.TryEnqueue(() => viewModel.Apply(update));
        };
        _services.Coordinator.ProgressChanged += progress;
        try
        {
            await new ProcessingDialog(viewModel) { XamlRoot = RootFrame.XamlRoot }.ShowAsync();
            if (attempted)
            {
                var persisted = await _services.TryRefreshTrackedJobAsync(jobId, CancellationToken.None);
                if (persisted && !viewModel.HasError)
                {
                    lecture.TranscriptText = await LoadTranscriptTextAsync(recording.Id, CancellationToken.None);
                }
            }
        }
        finally
        {
            _services.Coordinator.ProgressChanged -= progress;
        }
    }

    private void NavigateSettings()
    {
        BeginLibraryNavigation(LibraryDestination.Settings, navigationItem: null);
        RootFrame.Content = new SettingsPage(
            new SettingsViewModel(_services.Settings, _services.StorageLocations),
            ApplyNightMode,
            PickFolderAsync);
    }

    private void ApplyNightMode(bool enabled) =>
        NavigationRoot.RequestedTheme = enabled ? ElementTheme.Dark : ElementTheme.Light;

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
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

        RootFrame.Content = new CompletionPage(viewModel, ShowJoin, assign, _services.Paths.RecordingsRoot);
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

    private sealed class DisabledNoticePresenter : ICloudNoticePresenter
    {
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }

    private async Task<string> LoadTranscriptTextAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        var checkpoint = await _services.Repository.GetTranscriptAsync(recordingId, cancellationToken);
        var bytes = await _services.Artifacts.ReadVerifiedAsync(checkpoint, cancellationToken);
        var transcript = bytes is null ? null : JsonSerializer.Deserialize<Transcript>(bytes.Value.Span);
        return transcript?.Text ?? string.Empty;
    }
}
