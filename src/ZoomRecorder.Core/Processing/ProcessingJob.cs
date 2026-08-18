using System.Text;

namespace ZoomRecorder.Core.Processing;

public sealed class ProcessingJob
{
    private ProcessingJob(Guid id, Guid recordingId, bool deleteVideo, DateTimeOffset startedAt)
    {
        Id = id;
        RecordingId = recordingId;
        DeleteVideo = deleteVideo;
        StartedAt = startedAt;
        UpdatedAt = startedAt;
    }

    public Guid Id { get; }

    public Guid RecordingId { get; }

    public bool DeleteVideo { get; }

    public ProcessingState State { get; private set; } = ProcessingState.ReadyToProcess;

    public ProcessingState? FailedStage { get; private set; }

    public string? ErrorCode { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool TranscriptCommitted { get; private set; }

    public bool LecturePackageCommitted { get; private set; }

    public bool AssignmentsCommitted { get; private set; }

    public bool ClassGuideOutcomeRecorded { get; private set; }

    public static ProcessingJob Start(Guid jobId, Guid recordingId, bool deleteVideo, DateTimeOffset now)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A processing job id is required.", nameof(jobId));
        }

        if (recordingId == Guid.Empty)
        {
            throw new ArgumentException("A recording id is required.", nameof(recordingId));
        }

        return new ProcessingJob(jobId, recordingId, deleteVideo, now);
    }

    public void TransitionTo(ProcessingState next, DateTimeOffset now)
    {
        EnsureTimestamp(now);

        var expected = State switch
        {
            ProcessingState.ReadyToProcess => ProcessingState.PreparingAudio,
            ProcessingState.PreparingAudio => ProcessingState.Transcribing,
            ProcessingState.Transcribing => ProcessingState.GeneratingStudyPackage,
            ProcessingState.GeneratingStudyPackage => ProcessingState.UpdatingClassGuide,
            ProcessingState.UpdatingClassGuide => ProcessingState.Completed,
            _ => (ProcessingState?)null
        };

        if (expected != next || next == ProcessingState.Completed && !CanComplete())
        {
            throw new InvalidProcessingTransitionException(State, next);
        }

        State = next;
        UpdatedAt = now;
        if (next == ProcessingState.Completed)
        {
            CompletedAt = now;
        }
    }

    public void MarkNeedsAttention(string errorCode, DateTimeOffset now)
    {
        EnsureTimestamp(now);
        if (!IsActive(State))
        {
            throw new InvalidProcessingTransitionException(State, ProcessingState.NeedsAttention);
        }

        FailedStage = State;
        ErrorCode = SanitizeErrorCode(errorCode);
        State = ProcessingState.NeedsAttention;
        UpdatedAt = now;
    }

    public void Retry(DateTimeOffset now)
    {
        EnsureTimestamp(now);
        if (State != ProcessingState.NeedsAttention || FailedStage is null)
        {
            throw new InvalidProcessingTransitionException(State, FailedStage ?? ProcessingState.ReadyToProcess);
        }

        State = FailedStage.Value;
        FailedStage = null;
        ErrorCode = null;
        UpdatedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        EnsureTimestamp(now);
        if (!IsActive(State) && State != ProcessingState.NeedsAttention)
        {
            throw new InvalidProcessingTransitionException(State, ProcessingState.Cancelled);
        }

        State = ProcessingState.Cancelled;
        UpdatedAt = now;
    }

    public void MarkTranscriptCommitted(DateTimeOffset now) =>
        MarkCommit(now, () => TranscriptCommitted = true);

    public void MarkLecturePackageCommitted(DateTimeOffset now) =>
        MarkCommit(now, () => LecturePackageCommitted = true);

    public void MarkAssignmentsCommitted(DateTimeOffset now) =>
        MarkCommit(now, () => AssignmentsCommitted = true);

    public void RecordClassGuideOutcome(DateTimeOffset now) =>
        MarkCommit(now, () => ClassGuideOutcomeRecorded = true);

    private static bool IsActive(ProcessingState state) =>
        state is ProcessingState.ReadyToProcess
            or ProcessingState.PreparingAudio
            or ProcessingState.Transcribing
            or ProcessingState.GeneratingStudyPackage
            or ProcessingState.UpdatingClassGuide;

    private bool CanComplete() =>
        TranscriptCommitted && LecturePackageCommitted && AssignmentsCommitted && ClassGuideOutcomeRecorded;

    private void MarkCommit(DateTimeOffset now, Action mark)
    {
        EnsureTimestamp(now);
        if (!IsActive(State) && State != ProcessingState.NeedsAttention)
        {
            throw new InvalidProcessingTransitionException(State, State);
        }

        mark();
        UpdatedAt = now;
    }

    private void EnsureTimestamp(DateTimeOffset now)
    {
        if (now < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Processing timestamps cannot move backwards.");
        }
    }

    private static string SanitizeErrorCode(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        var result = new StringBuilder(capacity: Math.Min(errorCode.Length, 128));
        var separatorPending = false;
        foreach (var character in errorCode.Trim())
        {
            if (character is ':' or '<')
            {
                break;
            }

            if (result.Length >= 128)
            {
                break;
            }

            if (char.IsLetterOrDigit(character) || character is '.' or '_' or '-')
            {
                if (separatorPending && result.Length > 0 && result[^1] != '-')
                {
                    result.Append('-');
                }

                result.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else if (char.IsWhiteSpace(character))
            {
                separatorPending = true;
            }
        }

        if (result.Length == 0)
        {
            throw new ArgumentException("The error code must contain letters or digits.", nameof(errorCode));
        }

        return result.ToString().TrimEnd('-');
    }
}

public sealed class InvalidProcessingTransitionException(ProcessingState current, ProcessingState requested)
    : InvalidOperationException($"Cannot move processing job from {current} to {requested}.")
{
    public ProcessingState Current { get; } = current;

    public ProcessingState Requested { get; } = requested;
}
