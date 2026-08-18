using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.Core.Library;

public sealed class RecordingAssignmentService
{
    private readonly ILibraryRepository repository;
    private readonly Func<DateTimeOffset> clock;

    public RecordingAssignmentService(ILibraryRepository repository, Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(clock);
        this.repository = repository;
        this.clock = clock;
    }

    public async Task<RecordingRecord> RegisterFinalizedAsync(
        RecordingResult result,
        string? meetingId,
        Guid? explicitClassId,
        bool rememberExplicitChoice,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(result.Path);
        ArgumentOutOfRangeException.ThrowIfNegative(result.ByteSize);
        if (result.Duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(result), "Recording duration cannot be negative.");
        }

        var normalizedMeetingId = NormalizeMeetingId(meetingId);
        var classId = explicitClassId;

        if (classId is null && normalizedMeetingId is not null)
        {
            var mapping = await repository.FindMappingAsync(normalizedMeetingId, cancellationToken);
            classId = mapping?.ClassId;
        }

        var recording = new RecordingRecord(
            Guid.NewGuid(),
            classId,
            Path.GetFullPath(result.Path),
            Path.GetFileName(result.Path),
            normalizedMeetingId,
            clock(),
            result.Duration,
            result.ByteSize,
            VideoAvailable: true);

        var savedRecording = await repository.AddRecordingAsync(recording, cancellationToken);

        if (rememberExplicitChoice && explicitClassId is not null && normalizedMeetingId is not null)
        {
            await repository.UpsertMappingAsync(
                new MeetingClassMapping(normalizedMeetingId, explicitClassId.Value), cancellationToken);
        }

        return savedRecording;
    }

    private static string? NormalizeMeetingId(string? meetingId)
    {
        var normalized = meetingId?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
