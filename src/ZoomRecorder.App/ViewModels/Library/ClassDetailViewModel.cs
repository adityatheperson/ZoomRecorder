using System.Collections.ObjectModel;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed class ClassDetailViewModel : LibraryViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly Guid _classId;
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
}
