using System.Text.Json;

namespace ZoomRecorder.Core.Processing;

public sealed class ProcessingCoordinator
{
    private readonly IProcessingJobStore jobs;
    private readonly IAudioChunkPreparer audio;
    private readonly ITranscriptionClient transcription;
    private readonly IStudyGenerationClient studyGeneration;
    private readonly IProcessingArtifactStore artifacts;
    private readonly long maxChunkBytes;
    private readonly object activeGate = new();
    private readonly Dictionary<Guid, ActiveOperation> active = [];

    public ProcessingCoordinator(
        IProcessingJobStore jobs,
        IAudioChunkPreparer audio,
        ITranscriptionClient transcription,
        IStudyGenerationClient studyGeneration,
        IProcessingArtifactStore artifacts,
        long maxChunkBytes)
    {
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.transcription = transcription ?? throw new ArgumentNullException(nameof(transcription));
        this.studyGeneration = studyGeneration ?? throw new ArgumentNullException(nameof(studyGeneration));
        this.artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        if (maxChunkBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChunkBytes));
        }

        this.maxChunkBytes = maxChunkBytes;
    }

    public event EventHandler<ProcessingProgress>? ProgressChanged;

    public async Task StartAsync(ProcessingRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.JobDirectory);
        await PersistAsync(() => jobs.CreateAsync(request, CancellationToken.None));
        await RunExclusiveAsync(request.JobId, cancellationToken);
    }

    public Task ResumeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        ValidateId(jobId, nameof(jobId));
        return RunExclusiveAsync(jobId, cancellationToken);
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        ValidateId(jobId, nameof(jobId));
        ActiveOperation? operation;
        lock (activeGate)
        {
            active.TryGetValue(jobId, out operation);
            operation?.Stop.Cancel();
        }

        if (operation is not null)
        {
            await operation.Completion.Task.WaitAsync(cancellationToken);
            return;
        }

        var job = await jobs.LoadAsync(jobId, cancellationToken);
        if (job.State == ProcessingState.Cancelled)
        {
            return;
        }

        if (job.State == ProcessingState.Completed)
        {
            throw new InvalidProcessingTransitionException(job.State, ProcessingState.Cancelled);
        }

        job = await PersistAsync(() => jobs.CancelAsync(jobId, job.Revision, CancellationToken.None));
        await CleanupAsync(job, CancellationToken.None);
        await PublishAsync(job, CancellationToken.None);
    }

    public Task RetryGuideAsync(Guid jobId, CancellationToken cancellationToken)
    {
        ValidateId(jobId, nameof(jobId));
        return RunExclusiveAsync(jobId, cancellationToken, RetryGuideCoreAsync);
    }

    private async Task RetryGuideCoreAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.LoadAsync(jobId, cancellationToken);
        if (job.State != ProcessingState.Completed || !job.GuideUpdatePending)
        {
            throw new InvalidProcessingTransitionException(job.State, ProcessingState.UpdatingClassGuide);
        }

        var guide = await GenerateGuideArtifactAsync(job, cancellationToken);
        job = await PersistAsync(() => jobs.CompleteGuideAsync(
            jobId,
            job.Revision,
            ClassGuideOutcome.Succeeded,
            guide,
            CancellationToken.None));
        await PublishAsync(job, cancellationToken);
    }

    public async Task<IReadOnlyList<ProcessingJobSnapshot>> RecoverAsync(CancellationToken cancellationToken)
    {
        var resumable = await jobs.ListResumableAsync(cancellationToken);
        foreach (var job in resumable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CleanupAsync(job, cancellationToken);
        }

        return resumable;
    }

    private Task RunExclusiveAsync(Guid jobId, CancellationToken callerCancellation) =>
        RunExclusiveAsync(jobId, callerCancellation, ExecuteAsync);

    private async Task RunExclusiveAsync(
        Guid jobId,
        CancellationToken callerCancellation,
        Func<Guid, CancellationToken, Task> action)
    {
        var operation = new ActiveOperation();
        lock (activeGate)
        {
            if (!active.TryAdd(jobId, operation))
            {
                throw new ProcessingConcurrencyException(jobId);
            }
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, operation.Stop.Token);
            try
            {
                await action(jobId, linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                await CancelAfterInterruptionAsync(jobId);
                if (callerCancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException(callerCancellation);
                }

                throw;
            }
        }
        finally
        {
            lock (activeGate)
            {
                active.Remove(jobId);
            }

            operation.Stop.Dispose();
            operation.Completion.TrySetResult();
        }
    }

    private async Task ExecuteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.LoadAsync(jobId, cancellationToken);
        if (job.State == ProcessingState.Cancelled)
        {
            return;
        }
        if (job.State == ProcessingState.Completed)
        {
            await PublishAsync(job, cancellationToken);
            return;
        }
        if (job.State == ProcessingState.NeedsAttention)
        {
            job = await PersistAsync(() => jobs.RetryAsync(jobId, job.Revision, CancellationToken.None));
            await PublishAsync(job, cancellationToken);
        }

        try
        {
            if (job.State == ProcessingState.ReadyToProcess)
            {
                job = await PersistAsync(() => jobs.MoveAsync(
                    jobId, job.Revision, ProcessingState.ReadyToProcess, ProcessingState.PreparingAudio,
                    CancellationToken.None));
                await PublishAsync(job, cancellationToken);
            }

            if (job.State == ProcessingState.PreparingAudio)
            {
                job = await PrepareAudioAsync(job, cancellationToken);
                job = await PersistAsync(() => jobs.MoveAsync(
                    jobId, job.Revision, ProcessingState.PreparingAudio, ProcessingState.Transcribing,
                    CancellationToken.None));
                await PublishAsync(job, cancellationToken);
            }

            if (job.State == ProcessingState.Transcribing)
            {
                job = await TranscribeAsync(job, cancellationToken);
                job = await PersistAsync(() => jobs.MoveAsync(
                    jobId, job.Revision, ProcessingState.Transcribing, ProcessingState.GeneratingStudyPackage,
                    CancellationToken.None));
                await PublishAsync(job, cancellationToken);
            }

            if (job.State == ProcessingState.GeneratingStudyPackage)
            {
                job = await GenerateLectureAsync(job, cancellationToken);
                job = await PersistAsync(() => jobs.MoveAsync(
                    jobId, job.Revision, ProcessingState.GeneratingStudyPackage, ProcessingState.UpdatingClassGuide,
                    CancellationToken.None));
                await PublishAsync(job, cancellationToken);
            }

            if (job.State == ProcessingState.UpdatingClassGuide)
            {
                job = await UpdateGuideAsync(job, cancellationToken);
                await PublishAsync(job, cancellationToken);
            }
        }
        catch (ProcessingConcurrencyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProcessingOperationException failure)
        {
            var latest = await PersistFailureStateAsync(() => jobs.LoadAsync(jobId, CancellationToken.None));
            if (latest.State is not ProcessingState.Completed and not ProcessingState.Cancelled and not ProcessingState.NeedsAttention)
            {
                job = await PersistFailureStateAsync(() => jobs.MarkNeedsAttentionAsync(
                    jobId, latest.Revision, latest.State, failure.Code, CancellationToken.None));
                await PublishAsync(job, CancellationToken.None);
            }

            throw;
        }
    }

    private async Task<ProcessingJobSnapshot> PrepareAudioAsync(
        ProcessingJobSnapshot job,
        CancellationToken cancellationToken)
    {
        var chunks = await jobs.ListAudioChunksAsync(job.Request.JobId, cancellationToken);
        if (!await AreAudioChunksValidAsync(chunks, job.Request.JobDirectory, cancellationToken))
        {
            chunks = await CallAsync(
                () => audio.PrepareAsync(
                    job.Request.Mp4Path,
                    job.Request.JobDirectory,
                    maxChunkBytes,
                    cancellationToken),
                CloudProcessingErrorCode.AudioPreparationFailed,
                cancellationToken);
            if (!await AreAudioChunksValidAsync(chunks, job.Request.JobDirectory, cancellationToken))
            {
                throw new ProcessingOperationException(CloudProcessingErrorCode.AudioPreparationFailed);
            }

            job = await PersistAsync(() => jobs.SaveAudioChunksAsync(
                job.Request.JobId, job.Revision, chunks, CancellationToken.None));
            await PublishAsync(job, cancellationToken);
        }

        return job;
    }

    private async Task<ProcessingJobSnapshot> TranscribeAsync(
        ProcessingJobSnapshot job,
        CancellationToken cancellationToken)
    {
        var chunks = await jobs.ListAudioChunksAsync(job.Request.JobId, cancellationToken);
        if (!await AreAudioChunksValidAsync(chunks, job.Request.JobDirectory, cancellationToken))
        {
            chunks = await CallAsync(
                () => audio.PrepareAsync(
                    job.Request.Mp4Path,
                    job.Request.JobDirectory,
                    maxChunkBytes,
                    cancellationToken),
                CloudProcessingErrorCode.AudioPreparationFailed,
                cancellationToken);
            if (!await AreAudioChunksValidAsync(chunks, job.Request.JobDirectory, cancellationToken))
            {
                throw new ProcessingOperationException(CloudProcessingErrorCode.AudioPreparationFailed);
            }

            job = await PersistAsync(() => jobs.SaveAudioChunksAsync(
                job.Request.JobId, job.Revision, chunks, CancellationToken.None));
            await PublishAsync(job, cancellationToken);
        }

        var checkpoints = (await jobs.ListTranscriptChunksAsync(job.Request.JobId, cancellationToken))
            .ToDictionary(item => item.Index);
        var results = new List<TranscriptChunk>(chunks.Count);
        foreach (var chunk in chunks.OrderBy(item => item.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranscriptChunk? result = null;
            if (checkpoints.TryGetValue(chunk.Index, out var checkpoint) &&
                string.Equals(checkpoint.AudioSha256, chunk.Sha256, StringComparison.Ordinal))
            {
                result = await ReadAsync<TranscriptChunk>(checkpoint.Artifact, cancellationToken);
                if (result is not null && !IsValidTranscriptChunk(result, chunk))
                {
                    result = null;
                }
            }

            if (result is null)
            {
                result = await CallAsync(
                    () => transcription.TranscribeAsync(chunk, cancellationToken),
                    CloudProcessingErrorCode.TranscriptionUnavailable,
                    cancellationToken);
                var artifact = await PersistAsync(() => artifacts.WriteJobArtifactAsync(
                    job.Request,
                    $"transcript-chunk-{chunk.Index:D6}.json",
                    JsonSerializer.SerializeToUtf8Bytes(result),
                    CancellationToken.None));
                job = await PersistAsync(() => jobs.SaveTranscriptChunkAsync(
                    job.Request.JobId,
                    job.Revision,
                    new TranscriptChunkCheckpoint(chunk.Index, chunk.Sha256, artifact),
                    CancellationToken.None));
                await PublishAsync(job, cancellationToken);
            }

            results.Add(result);
        }

        if (!job.TranscriptCommitted)
        {
            Transcript merged;
            try
            {
                merged = TranscriptMerger.Merge(results);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProcessingOperationException(CloudProcessingErrorCode.TranscriptionUnavailable);
            }

            var transcriptArtifact = await PersistAsync(() => artifacts.WriteRecordingArtifactAsync(
                job.Request.RecordingId,
                $"transcript-{job.Request.JobId:D}.json",
                JsonSerializer.SerializeToUtf8Bytes(merged),
                CancellationToken.None));
            job = await PersistAsync(() => jobs.CommitTranscriptAsync(
                job.Request.JobId, job.Revision, transcriptArtifact, CancellationToken.None));
            await PublishAsync(job, cancellationToken);
        }

        return job;
    }

    private async Task<ProcessingJobSnapshot> GenerateLectureAsync(
        ProcessingJobSnapshot job,
        CancellationToken cancellationToken)
    {
        if (job.LecturePackageCommitted && job.AssignmentsCommitted)
        {
            return job;
        }

        var transcript = job.TranscriptArtifact is { } checkpoint
            ? await ReadAsync<Transcript>(checkpoint, cancellationToken)
            : null;
        if (transcript is null)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.StorageCommitFailed);
        }

        var package = await CallAsync(
            () => studyGeneration.GenerateLectureAsync(transcript, cancellationToken),
            CloudProcessingErrorCode.StudyGenerationUnavailable,
            cancellationToken);
        try
        {
            StudyPackageValidator.Validate(package);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.StudyGenerationUnavailable);
        }

        var packageArtifact = await PersistAsync(() => artifacts.WriteRecordingArtifactAsync(
            job.Request.RecordingId,
            $"lecture-package-{job.Request.JobId:D}.json",
            JsonSerializer.SerializeToUtf8Bytes(package),
            CancellationToken.None));
        job = await PersistAsync(() => jobs.CommitLecturePackageAsync(
            job.Request.JobId,
            job.Revision,
            packageArtifact,
            job.TranscriptArtifact!.Sha256,
            package.Assignments,
            CancellationToken.None));
        await PublishAsync(job, cancellationToken);
        return job;
    }

    private async Task<ProcessingJobSnapshot> UpdateGuideAsync(
        ProcessingJobSnapshot job,
        CancellationToken cancellationToken)
    {
        try
        {
            var guide = await GenerateGuideArtifactAsync(job, cancellationToken);
            return await PersistAsync(() => jobs.CompleteGuideAsync(
                job.Request.JobId,
                job.Revision,
                ClassGuideOutcome.Succeeded,
                guide,
                CancellationToken.None));
        }
        catch (ProcessingConcurrencyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await PersistAsync(() => jobs.CompleteGuideAsync(
                job.Request.JobId,
                job.Revision,
                ClassGuideOutcome.Pending,
                guide: null,
                CancellationToken.None));
        }
    }

    private async Task<ArtifactCheckpoint> GenerateGuideArtifactAsync(
        ProcessingJobSnapshot job,
        CancellationToken cancellationToken)
    {
        var packageArtifacts = await jobs.ListLecturePackageArtifactsAsync(job.Request.ClassId, cancellationToken);
        var packages = new List<StudyPackage>(packageArtifacts.Count);
        foreach (var packageArtifact in packageArtifacts)
        {
            var package = await ReadAsync<StudyPackage>(packageArtifact, cancellationToken);
            if (package is null)
            {
                throw new ProcessingOperationException(CloudProcessingErrorCode.StorageCommitFailed);
            }

            StudyPackageValidator.Validate(package);
            packages.Add(package);
        }

        if (packages.Count == 0)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.ClassGuideUpdateFailed);
        }

        var guide = await CallAsync(
            () => studyGeneration.GenerateGuideAsync(packages, cancellationToken),
            CloudProcessingErrorCode.ClassGuideUpdateFailed,
            cancellationToken);
        ValidateGuide(guide);
        return await PersistAsync(() => artifacts.WriteClassArtifactAsync(
            job.Request.ClassId,
            $"class-guide-{job.Request.JobId:D}.json",
            JsonSerializer.SerializeToUtf8Bytes(guide),
            CancellationToken.None));
    }

    private async Task<bool> AreAudioChunksValidAsync(
        IReadOnlyList<AudioChunk> chunks,
        string jobDirectory,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
        {
            return false;
        }

        var ordered = chunks.OrderBy(item => item.Index).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var chunk = ordered[index];
            if (chunk.Index != index ||
                chunk.StartMilliseconds < 0 ||
                chunk.EndMilliseconds <= chunk.StartMilliseconds ||
                chunk.ByteSize <= 0 ||
                chunk.ByteSize > maxChunkBytes ||
                !IsDirectChild(chunk.Path, jobDirectory) ||
                !await artifacts.VerifyAsync(
                    chunk.Path, chunk.Sha256, chunk.ByteSize, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidTranscriptChunk(TranscriptChunk result, AudioChunk source)
    {
        if (result.SchemaVersion != 1 ||
            result.Index != source.Index ||
            result.StartMilliseconds != source.StartMilliseconds ||
            result.EndMilliseconds != source.EndMilliseconds ||
            result.Segments is null)
        {
            return false;
        }

        return result.Segments.All(segment =>
            segment is not null &&
            segment.StartMilliseconds >= source.StartMilliseconds &&
            segment.EndMilliseconds >= segment.StartMilliseconds &&
            segment.EndMilliseconds <= source.EndMilliseconds &&
            !string.IsNullOrWhiteSpace(segment.Text));
    }

    private async Task CancelAfterInterruptionAsync(Guid jobId)
    {
        var job = await jobs.LoadAsync(jobId, CancellationToken.None);
        if (job.State is not ProcessingState.Completed and not ProcessingState.Cancelled)
        {
            job = await jobs.CancelAsync(jobId, job.Revision, CancellationToken.None);
            await CleanupAsync(job, CancellationToken.None);
            await PublishAsync(job, CancellationToken.None);
        }
    }

    private async Task CleanupAsync(ProcessingJobSnapshot job, CancellationToken cancellationToken)
    {
        var audioChunks = await jobs.ListAudioChunksAsync(job.Request.JobId, cancellationToken);
        var transcriptChunks = await jobs.ListTranscriptChunksAsync(job.Request.JobId, cancellationToken);
        var published = audioChunks.Select(item => item.Path)
            .Concat(transcriptChunks.Select(item => item.Artifact.Path))
            .ToArray();
        await artifacts.CleanupJobAsync(job.Request, published, cancellationToken);
    }

    private async Task PublishAsync(ProcessingJobSnapshot job, CancellationToken cancellationToken)
    {
        var chunks = await jobs.ListAudioChunksAsync(job.Request.JobId, cancellationToken);
        var completed = await jobs.ListTranscriptChunksAsync(job.Request.JobId, cancellationToken);
        var progress = new ProcessingProgress(
            job.Request.JobId,
            job.State,
            completed.Count,
            chunks.Count,
            job.GuideOutcome,
            job.ErrorCode);
        var handlers = ProgressChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ProcessingProgress> handler in handlers)
        {
            try
            {
                handler(this, progress);
            }
            catch (Exception)
            {
                // Progress observers cannot affect durable processing.
            }
        }
    }

    private async Task<T?> ReadAsync<T>(ArtifactCheckpoint checkpoint, CancellationToken cancellationToken)
        where T : class
    {
        var bytes = await artifacts.ReadVerifiedAsync(checkpoint, cancellationToken);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes.Value.Span);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<T> CallAsync<T>(
        Func<Task<T>> action,
        CloudProcessingErrorCode code,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ProcessingOperationException(code);
        }
    }

    private static async Task<T> PersistAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (ProcessingConcurrencyException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.StorageCommitFailed);
        }
    }

    private static async Task<T> PersistFailureStateAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.StorageCommitFailed);
        }
    }

    private static bool IsDirectChild(string path, string directory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(directory))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            return string.Equals(parent, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void ValidateGuide(ClassStudyGuide guide)
    {
        ArgumentNullException.ThrowIfNull(guide);
        if (guide.SchemaVersion != StudyPackageValidator.SupportedSchemaVersion || guide.Topics is null)
        {
            throw new ProcessingOperationException(CloudProcessingErrorCode.ClassGuideUpdateFailed);
        }

        foreach (var topic in guide.Topics)
        {
            if (topic is null || string.IsNullOrWhiteSpace(topic.Topic) ||
                topic.Contributions is null ||
                topic.Contributions.Any(string.IsNullOrWhiteSpace))
            {
                throw new ProcessingOperationException(CloudProcessingErrorCode.ClassGuideUpdateFailed);
            }
        }
    }

    private static void ValidateRequest(ProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateId(request.JobId, nameof(request.JobId));
        ValidateId(request.RecordingId, nameof(request.RecordingId));
        ValidateId(request.ClassId, nameof(request.ClassId));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Mp4Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.JobDirectory);
        if (!Path.IsPathFullyQualified(request.Mp4Path) || !Path.IsPathFullyQualified(request.JobDirectory))
        {
            throw new ArgumentException("Processing paths must be fully qualified.", nameof(request));
        }
    }

    private static void ValidateId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identifier is required.", name);
        }
    }

    private sealed class ActiveOperation
    {
        internal CancellationTokenSource Stop { get; } = new();
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
