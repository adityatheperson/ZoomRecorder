using System.Collections.ObjectModel;
using ZoomRecorder.App.Renaming;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed class ClassDetailViewModel : LibraryViewModelBase
{
    private const string DeletionUnavailableMessage =
        "The recording could not be deleted. Close any app using its files and try again.";

    private readonly ILibraryRepository _repository;
    private readonly Guid _classId;
    private readonly HashSet<Guid> _deletingRecordingIds = [];
    private readonly HashSet<Guid> _renamingRecordingIds = [];
    private ClassRecord? _classRecord;

    public ClassDetailViewModel(ILibraryRepository repository, Guid classId)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _classId = classId;
    }

    public Guid ClassId => _classId;
    public string ClassName => _classRecord?.Name ?? "Class";
    public string Term => string.IsNullOrWhiteSpace(_classRecord?.Term) ? "No term" : _classRecord.Term;
    public ObservableCollection<RecordingListItem> Lectures { get; } = [];
    public int LectureCount => Lectures.Count;
    private string? _deletionErrorMessage;
    private string? _renameErrorMessage;
    public string? DeletionErrorMessage => _deletionErrorMessage;
    public string? RenameErrorMessage => _renameErrorMessage;
    public bool IsDeleting(Guid recordingId) => _deletingRecordingIds.Contains(recordingId);

    public async Task<bool> DeleteAsync(
        RecordingListItem item,
        Func<RecordingRecord, CancellationToken, Task> deletion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(deletion);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Lectures.Any(lecture => lecture.Id == item.Id) || !_deletingRecordingIds.Add(item.Id))
        {
            return false;
        }

        try
        {
            await deletion(item.Recording, cancellationToken);
            var visibleItem = Lectures.SingleOrDefault(lecture => lecture.Id == item.Id);
            if (visibleItem is not null)
            {
                Lectures.Remove(visibleItem);
            }
            RaisePropertyChanged(nameof(LectureCount));
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
            _deletingRecordingIds.Remove(item.Id);
        }
    }

    public async Task<bool> RenameAsync(
        RecordingListItem item,
        string requestedName,
        Func<RecordingRecord, string, CancellationToken, Task<RecordingRecord>> rename,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(requestedName);
        ArgumentNullException.ThrowIfNull(rename);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Lectures.Any(lecture => lecture.Id == item.Id) || !_renamingRecordingIds.Add(item.Id))
        {
            return false;
        }

        try
        {
            var renamed = await rename(item.Recording, requestedName, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var visibleIndex = Lectures
                .Select((lecture, index) => (lecture, index))
                .Where(entry => entry.lecture.Id == item.Id)
                .Select(entry => entry.index)
                .DefaultIfEmpty(-1)
                .Single();
            if (visibleIndex >= 0)
            {
                Lectures[visibleIndex] = new RecordingListItem(
                    renamed,
                    Lectures[visibleIndex].ProcessingStatus);
            }
            SetRenameError(null);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RecordingRenameException exception)
        {
            SetRenameError(RecordingRenameErrorMessages.For(exception.Code));
            return false;
        }
        catch
        {
            SetRenameError(RecordingRenameErrorMessages.Unavailable);
            return false;
        }
        finally
        {
            _renamingRecordingIds.Remove(item.Id);
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            var classes = await _repository.ListClassesAsync(includeArchived: true, cancellationToken);
            var classRecord = classes.SingleOrDefault(item => item.Id == _classId);
            var lectures = await _repository.ListRecordingsAsync(_classId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _classRecord = classRecord;
            ReplaceLectures(lectures);
            RaisePropertyChanged(nameof(ClassName));
            RaisePropertyChanged(nameof(Term));
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

    public async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        BeginOperation();
        try
        {
            var normalized = query.Trim();
            var lectures = normalized.Length == 0
                ? await _repository.ListRecordingsAsync(_classId, cancellationToken)
                : await _repository.SearchClassRecordingsAsync(_classId, normalized, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            ReplaceLectures(lectures);
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

    private void ReplaceLectures(IEnumerable<RecordingRecord> lectures)
    {
        Replace(Lectures, lectures
            .Where(item => item.ClassId == _classId)
            .OrderByDescending(item => item.RecordedAt)
            .ThenBy(item => item.Id)
            .Select(item => new RecordingListItem(item)));
        RaisePropertyChanged(nameof(LectureCount));
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

    private void SetRenameError(string? message)
    {
        if (_renameErrorMessage == message)
        {
            return;
        }

        _renameErrorMessage = message;
        RaisePropertyChanged(nameof(RenameErrorMessage));
    }
}
