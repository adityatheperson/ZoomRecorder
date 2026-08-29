using System.Collections.ObjectModel;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed class RecordingsViewModel : LibraryViewModelBase
{
    private const string AssignmentUnavailableMessage =
        "Assignment is unavailable right now. Try again.";
    private const string DeletionUnavailableMessage =
        "The recording could not be deleted. Close any app using its files and try again.";

    private readonly ILibraryRepository _repository;
    private readonly Func<Guid, CancellationToken, Task<string>> processingStatus;
    private readonly HashSet<Guid> _deletingRecordingIds = [];
    private RecordingListItem? _assignmentRetryItem;
    private string? _assignmentErrorMessage;
    private string? _deletionErrorMessage;

    public RecordingsViewModel(
        ILibraryRepository repository,
        Func<Guid, CancellationToken, Task<string>>? processingStatus = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.processingStatus = processingStatus ?? ((_, _) => Task.FromResult("Not transcribed"));
    }

    public ObservableCollection<RecordingListItem> Recordings { get; } = [];
    public string? AssignmentErrorMessage => _assignmentErrorMessage;
    public bool CanRetryAssignment => _assignmentRetryItem is not null;
    public string? DeletionErrorMessage => _deletionErrorMessage;

    public Task LoadAsync(CancellationToken cancellationToken) =>
        LoadQueryAsync(query: null, cancellationToken);

    public Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return LoadQueryAsync(query.Trim(), cancellationToken);
    }

    public async Task<bool> AssignAsync(
        RecordingListItem item,
        Func<RecordingRecord, CancellationToken, Task<bool>> assignment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(assignment);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var assigned = await assignment(item.Recording, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (assigned)
            {
                ClearAssignmentFailure();
            }

            return assigned;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            SetAssignmentFailure(item);
            return false;
        }
    }

    public Task<bool> RetryAssignmentAsync(
        Func<RecordingRecord, CancellationToken, Task<bool>> assignment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return _assignmentRetryItem is null
            ? Task.FromResult(false)
            : AssignAsync(_assignmentRetryItem, assignment, cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        RecordingListItem item,
        Func<RecordingRecord, CancellationToken, Task> deletion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(deletion);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_deletingRecordingIds.Add(item.Recording.Id))
        {
            return false;
        }

        try
        {
            await deletion(item.Recording, cancellationToken);
            Recordings.Remove(item);
            if (_assignmentRetryItem?.Recording.Id == item.Recording.Id)
            {
                ClearAssignmentFailure();
            }
            SetDeletionError(null);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            SetDeletionError(DeletionUnavailableMessage);
            return false;
        }
        finally
        {
            _deletingRecordingIds.Remove(item.Recording.Id);
        }
    }

    private async Task LoadQueryAsync(string? query, CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            var recordings = await _repository.ListRecordingsAsync(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var ordered = recordings
                .Where(item => string.IsNullOrWhiteSpace(query) ||
                    item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.RecordedAt)
                .ThenBy(item => item.Id)
                .ToArray();
            var filtered = new List<RecordingListItem>(ordered.Length);
            foreach (var recording in ordered)
            {
                filtered.Add(new RecordingListItem(
                    recording,
                    await processingStatus(recording.Id, cancellationToken)));
            }
            Replace(Recordings, filtered);
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

    private void SetAssignmentFailure(RecordingListItem item)
    {
        var couldRetry = CanRetryAssignment;
        _assignmentRetryItem = item;
        if (_assignmentErrorMessage != AssignmentUnavailableMessage)
        {
            _assignmentErrorMessage = AssignmentUnavailableMessage;
            RaisePropertyChanged(nameof(AssignmentErrorMessage));
        }

        if (!couldRetry)
        {
            RaisePropertyChanged(nameof(CanRetryAssignment));
        }
    }

    private void ClearAssignmentFailure()
    {
        var couldRetry = CanRetryAssignment;
        _assignmentRetryItem = null;
        if (_assignmentErrorMessage is not null)
        {
            _assignmentErrorMessage = null;
            RaisePropertyChanged(nameof(AssignmentErrorMessage));
        }

        if (couldRetry)
        {
            RaisePropertyChanged(nameof(CanRetryAssignment));
        }
    }

    private void SetDeletionError(string? message)
    {
        if (_deletionErrorMessage == message)
        {
            return;
        }

        _deletionErrorMessage = message;
        RaisePropertyChanged(nameof(DeletionErrorMessage));
    }
}
