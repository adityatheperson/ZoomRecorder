using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed class AssignRecordingViewModel
{
    private readonly ILibraryRepository _repository;
    private readonly RecordingRecord _recording;
    private readonly string? _meetingId;

    public AssignRecordingViewModel(ILibraryRepository repository, RecordingRecord recording)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _meetingId = NormalizeOptional(recording.MeetingId);
    }

    public IReadOnlyList<ClassRecord> Classes { get; private set; } = [];
    public bool CanRememberMeeting => _meetingId is not null;

    public async Task<IReadOnlyList<ClassRecord>> LoadClassesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Classes = await _repository.ListClassesAsync(includeArchived: false, cancellationToken);
        return Classes;
    }

    public async Task AssignExistingAsync(
        Guid classId,
        bool rememberMeeting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _repository.AssignRecordingToClassAsync(
            _recording.Id,
            classId,
            rememberMeeting ? _meetingId : null,
            cancellationToken);
    }

    public async Task<ClassRecord> CreateAndAssignAsync(
        string name,
        string? term,
        bool rememberMeeting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Enter a class name.", nameof(name));
        }

        return await _repository.CreateClassAndAssignRecordingAsync(
            normalizedName,
            NormalizeOptional(term),
            _recording.Id,
            rememberMeeting ? _meetingId : null,
            cancellationToken);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
