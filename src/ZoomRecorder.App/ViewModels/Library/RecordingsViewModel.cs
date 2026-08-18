using System.Collections.ObjectModel;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed class RecordingsViewModel : LibraryViewModelBase
{
    private readonly ILibraryRepository _repository;

    public RecordingsViewModel(ILibraryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public ObservableCollection<RecordingListItem> Recordings { get; } = [];

    public Task LoadAsync(CancellationToken cancellationToken) =>
        LoadQueryAsync(query: null, cancellationToken);

    public Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return LoadQueryAsync(query.Trim(), cancellationToken);
    }

    private async Task LoadQueryAsync(string? query, CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            var recordings = await _repository.ListRecordingsAsync(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var filtered = recordings
                .Where(item => string.IsNullOrWhiteSpace(query) ||
                    item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.RecordedAt)
                .ThenBy(item => item.Id)
                .Select(item => new RecordingListItem(item))
                .ToArray();
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
}
