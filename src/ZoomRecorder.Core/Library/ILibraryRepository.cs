namespace ZoomRecorder.Core.Library;

public interface ILibraryRepository
{
    Task<ClassRecord> CreateClassAsync(string name, string? term, CancellationToken cancellationToken);
    Task<RecordingRecord> AddRecordingAsync(RecordingRecord recording, CancellationToken cancellationToken);
    Task<RecordingRecord?> FindRecordingByPathAsync(string canonicalPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClassRecord>> ListClassesAsync(bool includeArchived, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecordingRecord>> ListRecordingsAsync(Guid? classId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecordingRecord>> ListUnassignedRecordingsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RecordingRecord>> SearchClassRecordingsAsync(Guid classId, string query, CancellationToken cancellationToken);
    Task AssignRecordingAsync(Guid recordingId, Guid? classId, CancellationToken cancellationToken);
    Task AssignRecordingToClassAsync(
        Guid recordingId,
        Guid classId,
        string? meetingIdToRemember,
        CancellationToken cancellationToken);
    Task<ClassRecord> CreateClassAndAssignRecordingAsync(
        string name,
        string? term,
        Guid recordingId,
        string? meetingIdToRemember,
        CancellationToken cancellationToken);
    Task<MeetingClassMapping?> FindMappingAsync(string meetingId, CancellationToken cancellationToken);
    Task UpsertMappingAsync(MeetingClassMapping mapping, CancellationToken cancellationToken);
    Task ForgetMappingAsync(string meetingId, CancellationToken cancellationToken);
}
