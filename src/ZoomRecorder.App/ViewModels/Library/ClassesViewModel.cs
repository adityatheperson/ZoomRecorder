using System.Collections.ObjectModel;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed class ClassesViewModel : LibraryViewModelBase
{
    private readonly ILibraryRepository _repository;

    public ClassesViewModel(ILibraryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public ObservableCollection<ClassCardViewModel> Classes { get; } = [];
    public ObservableCollection<RecordingListItem> RecentRecordings { get; } = [];
    public ObservableCollection<RecordingListItem> UnassignedRecordings { get; } = [];
    public int UnassignedCount => UnassignedRecordings.Count;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            var classes = await _repository.ListClassesAsync(includeArchived: false, cancellationToken);
            var allRecordings = await _repository.ListRecordingsAsync(null, cancellationToken);
            var unassigned = await _repository.ListUnassignedRecordingsAsync(cancellationToken);
            var cards = new List<ClassCardViewModel>(classes.Count);
            foreach (var classRecord in classes.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                cards.Add(new ClassCardViewModel(
                    classRecord,
                    allRecordings
                        .Where(item => item.ClassId == classRecord.Id)
                        .OrderByDescending(item => item.RecordedAt)
                        .ToArray()));
            }

            cancellationToken.ThrowIfCancellationRequested();

            Replace(Classes, cards);
            Replace(RecentRecordings, allRecordings
                .OrderByDescending(item => item.RecordedAt)
                .ThenBy(item => item.Id)
                .Take(5)
                .Select(item => new RecordingListItem(item)));
            Replace(UnassignedRecordings, unassigned
                .OrderByDescending(item => item.RecordedAt)
                .ThenBy(item => item.Id)
                .Select(item => new RecordingListItem(item)));
            RaisePropertyChanged(nameof(UnassignedCount));
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
}
