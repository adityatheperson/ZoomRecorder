namespace ZoomRecorder.Core.Processing;

public sealed record ProcessingRequest(
    Guid JobId,
    Guid RecordingId,
    Guid ClassId,
    string Mp4Path,
    string JobDirectory,
    bool DeleteVideoAfterSuccess);

public sealed record ArtifactCheckpoint(string Path, string Sha256);

public sealed record TranscriptChunkCheckpoint(
    int Index,
    string AudioSha256,
    ArtifactCheckpoint Artifact);

public enum ClassGuideOutcome
{
    NotAttempted,
    Succeeded,
    Pending
}

public sealed record ProcessingJobSnapshot(
    ProcessingRequest Request,
    ProcessingState State,
    ProcessingState? FailedStage,
    CloudProcessingErrorCode? ErrorCode,
    bool TranscriptCommitted,
    ArtifactCheckpoint? TranscriptArtifact,
    bool LecturePackageCommitted,
    ArtifactCheckpoint? LecturePackageArtifact,
    bool AssignmentsCommitted,
    ClassGuideOutcome GuideOutcome,
    long Revision,
    DateTimeOffset UpdatedAt)
{
    public bool GuideUpdatePending => GuideOutcome == ClassGuideOutcome.Pending;

    public bool IsDeletionEligible =>
        State == ProcessingState.Completed &&
        TranscriptCommitted &&
        LecturePackageCommitted &&
        AssignmentsCommitted &&
        GuideOutcome is ClassGuideOutcome.Succeeded or ClassGuideOutcome.Pending;
}

public sealed class ProcessingProgress(
    Guid jobId,
    ProcessingState state,
    int completedChunks,
    int totalChunks,
    ClassGuideOutcome guideOutcome,
    CloudProcessingErrorCode? errorCode,
    TranscriptionActivity? transcriptionActivity = null,
    long? activityCompletedBytes = null,
    long? activityTotalBytes = null) : EventArgs
{
    public Guid JobId { get; } = jobId;
    public ProcessingState State { get; } = state;
    public int CompletedChunks { get; } = completedChunks;
    public int TotalChunks { get; } = totalChunks;
    public ClassGuideOutcome GuideOutcome { get; } = guideOutcome;
    public CloudProcessingErrorCode? ErrorCode { get; } = errorCode;
    public TranscriptionActivity? TranscriptionActivity { get; } = transcriptionActivity;
    public long? ActivityCompletedBytes { get; } = activityCompletedBytes;
    public long? ActivityTotalBytes { get; } = activityTotalBytes;
    public bool GuideUpdatePending => GuideOutcome == ClassGuideOutcome.Pending;
    public bool CanRetryGuide => State == ProcessingState.Completed && GuideUpdatePending;
}

public interface IProcessingJobStore
{
    Task<ProcessingJobSnapshot> CreateAsync(
        ProcessingRequest request,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> LoadAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> MoveAsync(
        Guid jobId,
        long expectedRevision,
        ProcessingState expectedState,
        ProcessingState nextState,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> SaveAudioChunksAsync(
        Guid jobId,
        long expectedRevision,
        IReadOnlyList<AudioChunk> chunks,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AudioChunk>> ListAudioChunksAsync(
        Guid jobId,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> SaveTranscriptChunkAsync(
        Guid jobId,
        long expectedRevision,
        TranscriptChunkCheckpoint chunk,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TranscriptChunkCheckpoint>> ListTranscriptChunksAsync(
        Guid jobId,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> CommitTranscriptAsync(
        Guid jobId,
        long expectedRevision,
        ArtifactCheckpoint transcript,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> CompleteTranscriptOnlyAsync(
        Guid jobId,
        long expectedRevision,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> CommitLecturePackageAsync(
        Guid jobId,
        long expectedRevision,
        ArtifactCheckpoint package,
        string sourceTranscriptSha256,
        IReadOnlyList<StudyAssignment> assignments,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactCheckpoint>> ListLecturePackageArtifactsAsync(
        Guid classId,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> CompleteGuideAsync(
        Guid jobId,
        long expectedRevision,
        ClassGuideOutcome outcome,
        ArtifactCheckpoint? guide,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> MarkNeedsAttentionAsync(
        Guid jobId,
        long expectedRevision,
        ProcessingState failedStage,
        CloudProcessingErrorCode errorCode,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> RetryAsync(
        Guid jobId,
        long expectedRevision,
        CancellationToken cancellationToken);

    Task<ProcessingJobSnapshot> CancelAsync(
        Guid jobId,
        long expectedRevision,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessingJobSnapshot>> ListResumableAsync(
        CancellationToken cancellationToken);
}

public interface IProcessingArtifactStore
{
    Task<bool> VerifyAsync(
        string path,
        string sha256,
        long? expectedByteSize,
        CancellationToken cancellationToken);

    Task<ArtifactCheckpoint> WriteJobArtifactAsync(
        ProcessingRequest request,
        string artifactName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    Task<ArtifactCheckpoint> WriteRecordingArtifactAsync(
        Guid recordingId,
        string artifactName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    Task<ArtifactCheckpoint> WriteClassArtifactAsync(
        Guid classId,
        string artifactName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    Task<ReadOnlyMemory<byte>?> ReadVerifiedAsync(
        ArtifactCheckpoint checkpoint,
        CancellationToken cancellationToken);

    Task CleanupJobAsync(
        ProcessingRequest request,
        IReadOnlyCollection<string> publishedPaths,
        CancellationToken cancellationToken);
}

public sealed class ProcessingConcurrencyException(Guid jobId)
    : InvalidOperationException("The processing job changed while this operation was in progress.")
{
    public Guid JobId { get; } = jobId;
}

public sealed class ProcessingOperationException : Exception
{
    public ProcessingOperationException(CloudProcessingErrorCode code)
        : base(MessageFor(code))
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown processing error code.");
        }

        Code = code;
    }

    public CloudProcessingErrorCode Code { get; }

    private static string MessageFor(CloudProcessingErrorCode code) => code switch
    {
        CloudProcessingErrorCode.AudioPreparationFailed => "Audio preparation could not be completed. Try again.",
        CloudProcessingErrorCode.TranscriptionUnavailable => "Transcription could not be completed. Try again.",
        CloudProcessingErrorCode.StudyGenerationUnavailable => "Study materials could not be generated. Try again.",
        CloudProcessingErrorCode.ClassGuideUpdateFailed => "The class guide could not be updated. Retry the guide update.",
        CloudProcessingErrorCode.StorageCommitFailed => "Processing progress could not be saved. Try again.",
        CloudProcessingErrorCode.CredentialUnavailable => "Cloud credentials are unavailable. Update them and try again.",
        CloudProcessingErrorCode.VideoRecycleFailed => "The completed video could not be moved to the Recycle Bin.",
        CloudProcessingErrorCode.ModelDownloadFailed => "The local transcription model could not be downloaded. Check your connection and try again.",
        CloudProcessingErrorCode.ModelVerificationFailed => "The local transcription model could not be verified. Delete the downloaded model and try again.",
        CloudProcessingErrorCode.LocalAudioConversionFailed => "The recording audio could not be converted for local transcription. Check available disk space and try again.",
        CloudProcessingErrorCode.LocalTranscriptionRuntimeFailed => "Local transcription could not run. Restart the app and try again.",
        CloudProcessingErrorCode.LocalTranscriptionOutputInvalid => "Local transcription produced invalid output. Try again; if it persists, reinstall the local transcription model.",
        _ => "Processing could not be completed. Try again."
    };
}
