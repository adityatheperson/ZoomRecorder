using System.Collections.ObjectModel;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.ViewModels.Library;

public enum LibraryDestination
{
    Home,
    Classes,
    Recordings,
    Settings,
    RecordClass,
    Meeting
}

public sealed class LibraryShellViewModel : LibraryViewModelBase
{
    private readonly ILibraryRepository _repository;
    private LibraryDestination _currentDestination = LibraryDestination.Home;
    private bool _isNavigationVisible = true;
    private int _unassignedCount;

    public LibraryShellViewModel(ILibraryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public ObservableCollection<RecordingListItem> RecentRecordings { get; } = [];

    public int UnassignedCount
    {
        get => _unassignedCount;
        private set
        {
            if (_unassignedCount == value) return;
            _unassignedCount = value;
            RaisePropertyChanged();
        }
    }

    public LibraryDestination CurrentDestination
    {
        get => _currentDestination;
        private set
        {
            if (_currentDestination == value) return;
            _currentDestination = value;
            RaisePropertyChanged();
        }
    }

    public bool IsNavigationVisible
    {
        get => _isNavigationVisible;
        private set
        {
            if (_isNavigationVisible == value) return;
            _isNavigationVisible = value;
            RaisePropertyChanged();
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            var recordings = await _repository.ListRecordingsAsync(null, cancellationToken);
            var unassigned = await _repository.ListUnassignedRecordingsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var recent = recordings
                .OrderByDescending(item => item.RecordedAt)
                .ThenBy(item => item.Id)
                .Take(5)
                .Select(item => new RecordingListItem(item))
                .ToArray();
            Replace(RecentRecordings, recent);
            UnassignedCount = unassigned.Count;
            CompleteOperation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            FailOperation();
        }
        finally
        {
            EndOperation();
        }
    }

    public void Navigate(LibraryDestination destination)
    {
        CurrentDestination = destination;
        IsNavigationVisible = destination != LibraryDestination.Meeting;
    }
}
