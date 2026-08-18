namespace ZoomRecorder.Core.Library;

public sealed record RecordingRecord(
    Guid Id, Guid? ClassId, string FilePath, string FileName, string? MeetingId,
    DateTimeOffset RecordedAt, TimeSpan Duration, long ByteSize, bool VideoAvailable);
