using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.Services;

public sealed class RecordingLibraryService
{
    private readonly ILibraryRepository _repository;
    private readonly RecordingAssignmentService _assignmentService;

    public RecordingLibraryService(ILibraryRepository repository, Func<DateTimeOffset> clock)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _assignmentService = new RecordingAssignmentService(repository, clock);
    }

    public async Task<RecordingRecord> RegisterFinalizedAsync(
        RecordingResult result,
        string? meetingId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        var canonicalPath = Path.GetFullPath(result.Path);
        var existing = await _repository.FindRecordingByPathAsync(canonicalPath, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            return await _assignmentService.RegisterFinalizedAsync(
                result,
                meetingId,
                explicitClassId: null,
                rememberExplicitChoice: false,
                cancellationToken);
        }
        catch
        {
            existing = await _repository.FindRecordingByPathAsync(canonicalPath, CancellationToken.None);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }
}
