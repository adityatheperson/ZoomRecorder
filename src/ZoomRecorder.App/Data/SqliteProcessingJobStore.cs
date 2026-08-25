using System.Globalization;
using Microsoft.Data.Sqlite;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Data;

public sealed class SqliteProcessingJobStore : IProcessingJobStore
{
    private readonly LibraryDatabase database;
    private readonly Func<DateTimeOffset> utcNow;

    public SqliteProcessingJobStore(LibraryDatabase database)
        : this(database, () => DateTimeOffset.UtcNow)
    {
    }

    internal SqliteProcessingJobStore(LibraryDatabase database, Func<DateTimeOffset> utcNow)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public Task<ProcessingJobSnapshot> CreateAsync(
        ProcessingRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var canonical = request with
        {
            Mp4Path = Path.GetFullPath(request.Mp4Path),
            JobDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.JobDirectory))
        };

        return WithTransactionAsync(async (connection, transaction) =>
        {
            var now = utcNow();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO processing_jobs(
                    id, recording_id, class_id, mp4_path, job_directory, state, failed_stage,
                    delete_video, error_code, transcript_committed, lecture_package_committed,
                    assignments_committed, guide_outcome, revision, created_at, updated_at)
                VALUES (
                    $id, $recordingId, $classId, $mp4Path, $jobDirectory, $state, NULL,
                    $deleteVideo, NULL, 0, 0, 0, $guideOutcome, 0, $createdAt, $updatedAt);
                """;
            command.Parameters.AddWithValue("$id", GuidText(canonical.JobId));
            command.Parameters.AddWithValue("$recordingId", GuidText(canonical.RecordingId));
            command.Parameters.AddWithValue("$classId", GuidText(canonical.ClassId));
            command.Parameters.AddWithValue("$mp4Path", canonical.Mp4Path);
            command.Parameters.AddWithValue("$jobDirectory", canonical.JobDirectory);
            command.Parameters.AddWithValue("$state", StateText(ProcessingState.ReadyToProcess));
            command.Parameters.AddWithValue("$deleteVideo", BooleanInteger(canonical.DeleteVideoAfterSuccess));
            command.Parameters.AddWithValue("$guideOutcome", GuideOutcomeText(ClassGuideOutcome.NotAttempted));
            command.Parameters.AddWithValue("$createdAt", TimestampText(now));
            command.Parameters.AddWithValue("$updatedAt", TimestampText(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return await ReadJobAsync(connection, transaction, canonical.JobId, cancellationToken);
        }, cancellationToken);
    }

    public Task<ProcessingJobSnapshot> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        ValidateId(jobId, nameof(jobId));
        return WithConnectionAsync(
            (connection, token) => ReadJobAsync(connection, transaction: null, jobId, token),
            cancellationToken);
    }

    public Task<ProcessingJobSnapshot> MoveAsync(
        Guid jobId,
        long expectedRevision,
        ProcessingState expectedState,
        ProcessingState nextState,
        CancellationToken cancellationToken)
    {
        ValidateRevision(expectedRevision);
        return WithTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadExpectedAsync(
                connection, transaction, jobId, expectedRevision, expectedState, cancellationToken);
            await ValidateForwardMoveAsync(connection, transaction, current, nextState, cancellationToken);
            await UpdateStateAsync(
                connection,
                transaction,
                jobId,
                expectedRevision,
                expectedState,
                "state = $nextState, failed_stage = NULL, error_code = NULL",
                command => command.Parameters.AddWithValue("$nextState", StateText(nextState)),
                cancellationToken);
            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);
    }

    public Task<ProcessingJobSnapshot> SaveAudioChunksAsync(
        Guid jobId,
        long expectedRevision,
        IReadOnlyList<AudioChunk> chunks,
        CancellationToken cancellationToken)
    {
        var ordered = ValidateAudioChunks(chunks);
        return WithTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadJobAsync(connection, transaction, jobId, cancellationToken);
            AssertRevision(current, expectedRevision);
            if (current.State is not ProcessingState.PreparingAudio and not ProcessingState.Transcribing)
            {
                throw new InvalidProcessingTransitionException(current.State, current.State);
            }

            ValidateAudioChunkLocations(ordered, current.Request.JobDirectory);

            var reusable = await ReadReusableTranscriptChunksAsync(
                connection, transaction, jobId, cancellationToken);
            await BumpRevisionAsync(
                connection, transaction, current, "state = state", null, cancellationToken);

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM audio_chunks WHERE job_id = $jobId;";
                delete.Parameters.AddWithValue("$jobId", GuidText(jobId));
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var chunk in ordered)
            {
                await InsertAudioChunkAsync(connection, transaction, jobId, chunk, cancellationToken);
                if (reusable.TryGetValue(chunk.Index, out var checkpoint) &&
                    string.Equals(checkpoint.AudioSha256, chunk.Sha256, StringComparison.Ordinal))
                {
                    await UpsertTranscriptChunkAsync(
                        connection, transaction, jobId, checkpoint, cancellationToken);
                }
            }

            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AudioChunk>> ListAudioChunksAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        WithConnectionAsync<IReadOnlyList<AudioChunk>>(async (connection, token) =>
        {
            await EnsureJobExistsAsync(connection, jobId, token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT chunk_index, artifact_path, start_ms, end_ms, sha256, byte_size
                FROM audio_chunks
                WHERE job_id = $jobId AND artifact_path IS NOT NULL AND byte_size IS NOT NULL
                ORDER BY chunk_index;
                """;
            command.Parameters.AddWithValue("$jobId", GuidText(jobId));
            await using var reader = await command.ExecuteReaderAsync(token);
            var result = new List<AudioChunk>();
            while (await reader.ReadAsync(token))
            {
                result.Add(new AudioChunk(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetInt64(5)));
            }

            return result;
        }, cancellationToken);

    public Task<ProcessingJobSnapshot> SaveTranscriptChunkAsync(
        Guid jobId,
        long expectedRevision,
        TranscriptChunkCheckpoint chunk,
        CancellationToken cancellationToken)
    {
        ValidateTranscriptChunk(chunk);
        return WithTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadExpectedAsync(
                connection,
                transaction,
                jobId,
                expectedRevision,
                ProcessingState.Transcribing,
                cancellationToken);
            await EnsureAudioHashAsync(
                connection, transaction, jobId, chunk.Index, chunk.AudioSha256, cancellationToken);
            await BumpRevisionAsync(
                connection, transaction, current, "state = state", null, cancellationToken);
            await UpsertTranscriptChunkAsync(connection, transaction, jobId, chunk, cancellationToken);
            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TranscriptChunkCheckpoint>> ListTranscriptChunksAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        WithConnectionAsync<IReadOnlyList<TranscriptChunkCheckpoint>>(async (connection, token) =>
        {
            await EnsureJobExistsAsync(connection, jobId, token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT chunk_index, audio_sha256, artifact_path, artifact_sha256
                FROM transcription_chunks
                WHERE job_id = $jobId
                ORDER BY chunk_index;
                """;
            command.Parameters.AddWithValue("$jobId", GuidText(jobId));
            await using var reader = await command.ExecuteReaderAsync(token);
            var result = new List<TranscriptChunkCheckpoint>();
            while (await reader.ReadAsync(token))
            {
                result.Add(new TranscriptChunkCheckpoint(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    new ArtifactCheckpoint(reader.GetString(2), reader.GetString(3))));
            }

            return result;
        }, cancellationToken);

    public Task<ProcessingJobSnapshot> CommitTranscriptAsync(
        Guid jobId,
        long expectedRevision,
        ArtifactCheckpoint transcript,
        CancellationToken cancellationToken)
    {
        ValidateArtifact(transcript);
        return WithTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadExpectedAsync(
                connection, transaction, jobId, expectedRevision, ProcessingState.Transcribing, cancellationToken);
            await BumpRevisionAsync(
                connection,
                transaction,
                current,
                "transcript_committed = 1",
                null,
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO processing_transcripts(job_id, artifact_path, artifact_sha256)
                VALUES ($jobId, $path, $hash)
                ON CONFLICT(job_id) DO UPDATE SET
                    artifact_path = excluded.artifact_path,
                    artifact_sha256 = excluded.artifact_sha256;
                """;
            command.Parameters.AddWithValue("$jobId", GuidText(jobId));
            command.Parameters.AddWithValue("$path", transcript.Path);
            command.Parameters.AddWithValue("$hash", transcript.Sha256);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);
    }

    public Task<ProcessingJobSnapshot> CompleteTranscriptOnlyAsync(
        Guid jobId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ValidateId(jobId, nameof(jobId));
        ValidateRevision(expectedRevision);
        return WithTransactionAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE processing_jobs
                SET state = $completed,
                    failed_stage = NULL,
                    error_code = NULL,
                    revision = revision + 1,
                    updated_at = $updatedAt
                WHERE id = $jobId
                  AND revision = $expectedRevision
                  AND transcript_committed = 1
                  AND (
                    state IN ($transcribing, $generating, $updating)
                    OR (state = $needsAttention AND failed_stage IN ($transcribing, $generating, $updating))
                  );
                """;
            command.Parameters.AddWithValue("$completed", StateText(ProcessingState.Completed));
            command.Parameters.AddWithValue("$updatedAt", TimestampText(utcNow()));
            command.Parameters.AddWithValue("$jobId", GuidText(jobId));
            command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            command.Parameters.AddWithValue("$transcribing", StateText(ProcessingState.Transcribing));
            command.Parameters.AddWithValue("$generating", StateText(ProcessingState.GeneratingStudyPackage));
            command.Parameters.AddWithValue("$updating", StateText(ProcessingState.UpdatingClassGuide));
            command.Parameters.AddWithValue("$needsAttention", StateText(ProcessingState.NeedsAttention));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new ProcessingConcurrencyException(jobId);
            }

            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);
    }

    public Task<ProcessingJobSnapshot> CommitLecturePackageAsync(
        Guid jobId,
        long expectedRevision,
        ArtifactCheckpoint package,
        string sourceTranscriptSha256,
        IReadOnlyList<StudyAssignment> assignments,
        CancellationToken cancellationToken)
    {
        ValidateArtifact(package);
        ValidateHash(sourceTranscriptSha256, nameof(sourceTranscriptSha256));
        ValidateAssignments(assignments);

        return WithTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadExpectedAsync(
                connection,
                transaction,
                jobId,
                expectedRevision,
                ProcessingState.GeneratingStudyPackage,
                cancellationToken);
            var now = utcNow();
            await using (var packageCommand = connection.CreateCommand())
            {
                packageCommand.Transaction = transaction;
                packageCommand.CommandText = """
                    INSERT INTO lecture_packages(
                        recording_id, schema_version, artifact_path, artifact_sha256,
                        source_transcript_hash, is_stale, updated_at)
                    VALUES ($recordingId, 1, $path, $artifactHash, $sourceHash, 0, $updatedAt)
                    ON CONFLICT(recording_id) DO UPDATE SET
                        schema_version = excluded.schema_version,
                        artifact_path = excluded.artifact_path,
                        artifact_sha256 = excluded.artifact_sha256,
                        source_transcript_hash = excluded.source_transcript_hash,
                        is_stale = excluded.is_stale,
                        updated_at = excluded.updated_at;
                    """;
                packageCommand.Parameters.AddWithValue("$recordingId", GuidText(current.Request.RecordingId));
                packageCommand.Parameters.AddWithValue("$path", package.Path);
                packageCommand.Parameters.AddWithValue("$artifactHash", package.Sha256);
                packageCommand.Parameters.AddWithValue("$sourceHash", sourceTranscriptSha256);
                packageCommand.Parameters.AddWithValue("$updatedAt", TimestampText(now));
                await packageCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM assignments WHERE recording_id = $recordingId;";
                delete.Parameters.AddWithValue("$recordingId", GuidText(current.Request.RecordingId));
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var index = 0; index < assignments.Count; index++)
            {
                await InsertAssignmentAsync(
                    connection,
                    transaction,
                    current.Request.RecordingId,
                    assignments[index],
                    index,
                    cancellationToken);
            }

            await BumpRevisionAsync(
                connection,
                transaction,
                current,
                "lecture_package_committed = 1, assignments_committed = 1",
                null,
                cancellationToken);
            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ArtifactCheckpoint>> ListLecturePackageArtifactsAsync(
        Guid classId,
        CancellationToken cancellationToken)
    {
        ValidateId(classId, nameof(classId));
        return WithConnectionAsync<IReadOnlyList<ArtifactCheckpoint>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT packages.artifact_path, packages.artifact_sha256
                FROM lecture_packages packages
                INNER JOIN recordings ON recordings.id = packages.recording_id
                WHERE recordings.class_id = $classId
                ORDER BY recordings.recorded_at, recordings.id;
                """;
            command.Parameters.AddWithValue("$classId", GuidText(classId));
            await using var reader = await command.ExecuteReaderAsync(token);
            var result = new List<ArtifactCheckpoint>();
            while (await reader.ReadAsync(token))
            {
                result.Add(new ArtifactCheckpoint(reader.GetString(0), reader.GetString(1)));
            }

            return result;
        }, cancellationToken);
    }

    public Task<ProcessingJobSnapshot> CompleteGuideAsync(
        Guid jobId,
        long expectedRevision,
        ClassGuideOutcome outcome,
        ArtifactCheckpoint? guide,
        CancellationToken cancellationToken)
    {
        if (outcome is not ClassGuideOutcome.Succeeded and not ClassGuideOutcome.Pending)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        if (outcome == ClassGuideOutcome.Succeeded)
        {
            ValidateArtifact(guide ?? throw new ArgumentNullException(nameof(guide)));
        }
        else if (guide is not null)
        {
            throw new ArgumentException("A pending guide update cannot publish a guide artifact.", nameof(guide));
        }

        return WithTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadJobAsync(connection, transaction, jobId, cancellationToken);
            AssertRevision(current, expectedRevision);
            if (current.State != ProcessingState.UpdatingClassGuide &&
                !(current.State == ProcessingState.Completed && current.GuideOutcome == ClassGuideOutcome.Pending))
            {
                throw new InvalidProcessingTransitionException(current.State, ProcessingState.Completed);
            }

            var now = utcNow();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                if (outcome == ClassGuideOutcome.Pending)
                {
                    command.CommandText = """
                        INSERT INTO class_study_guides(
                            class_id, schema_version, artifact_path, artifact_sha256,
                            is_update_pending, updated_at)
                        VALUES ($classId, 1, '', '', 1, $updatedAt)
                        ON CONFLICT(class_id) DO UPDATE SET
                            is_update_pending = 1,
                            updated_at = excluded.updated_at;
                        """;
                }
                else
                {
                    command.CommandText = """
                        INSERT INTO class_study_guides(
                            class_id, schema_version, artifact_path, artifact_sha256,
                            is_update_pending, updated_at)
                        VALUES ($classId, 1, $path, $hash, 0, $updatedAt)
                        ON CONFLICT(class_id) DO UPDATE SET
                            schema_version = excluded.schema_version,
                            artifact_path = excluded.artifact_path,
                            artifact_sha256 = excluded.artifact_sha256,
                            is_update_pending = excluded.is_update_pending,
                            updated_at = excluded.updated_at;
                        """;
                    command.Parameters.AddWithValue("$path", guide!.Path);
                    command.Parameters.AddWithValue("$hash", guide.Sha256);
                }

                command.Parameters.AddWithValue("$classId", GuidText(current.Request.ClassId));
                command.Parameters.AddWithValue("$updatedAt", TimestampText(now));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await BumpRevisionAsync(
                connection,
                transaction,
                current,
                "state = $completed, guide_outcome = $guideOutcome",
                command =>
                {
                    command.Parameters.AddWithValue("$completed", StateText(ProcessingState.Completed));
                    command.Parameters.AddWithValue("$guideOutcome", GuideOutcomeText(outcome));
                },
                cancellationToken);
            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);
    }

    public Task<ProcessingJobSnapshot> MarkNeedsAttentionAsync(
        Guid jobId,
        long expectedRevision,
        ProcessingState failedStage,
        CloudProcessingErrorCode errorCode,
        CancellationToken cancellationToken)
    {
        if (!IsActive(failedStage))
        {
            throw new ArgumentOutOfRangeException(nameof(failedStage));
        }
        if (!Enum.IsDefined(errorCode))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }

        return WithTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadExpectedAsync(
                connection, transaction, jobId, expectedRevision, failedStage, cancellationToken);
            await BumpRevisionAsync(
                connection,
                transaction,
                current,
                "state = $needsAttention, failed_stage = $failedStage, error_code = $errorCode",
                command =>
                {
                    command.Parameters.AddWithValue("$needsAttention", StateText(ProcessingState.NeedsAttention));
                    command.Parameters.AddWithValue("$failedStage", StateText(failedStage));
                    command.Parameters.AddWithValue("$errorCode", errorCode.ToString());
                },
                cancellationToken);
            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);
    }

    public Task<ProcessingJobSnapshot> RetryAsync(
        Guid jobId,
        long expectedRevision,
        CancellationToken cancellationToken) =>
        WithTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadExpectedAsync(
                connection,
                transaction,
                jobId,
                expectedRevision,
                ProcessingState.NeedsAttention,
                cancellationToken);
            if (current.FailedStage is not { } failedStage || !IsActive(failedStage))
            {
                throw new InvalidDataException("The processing job has no valid retry stage.");
            }

            await BumpRevisionAsync(
                connection,
                transaction,
                current,
                "state = $retryStage, failed_stage = NULL, error_code = NULL",
                command => command.Parameters.AddWithValue("$retryStage", StateText(failedStage)),
                cancellationToken);
            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);

    public Task<ProcessingJobSnapshot> CancelAsync(
        Guid jobId,
        long expectedRevision,
        CancellationToken cancellationToken) =>
        WithTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadJobAsync(connection, transaction, jobId, cancellationToken);
            AssertRevision(current, expectedRevision);
            if (!IsActive(current.State) && current.State != ProcessingState.NeedsAttention)
            {
                throw new InvalidProcessingTransitionException(current.State, ProcessingState.Cancelled);
            }

            await BumpRevisionAsync(
                connection,
                transaction,
                current,
                "state = $cancelled",
                command => command.Parameters.AddWithValue("$cancelled", StateText(ProcessingState.Cancelled)),
                cancellationToken);
            return await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        }, cancellationToken);

    public Task<IReadOnlyList<ProcessingJobSnapshot>> ListResumableAsync(
        CancellationToken cancellationToken) =>
        WithConnectionAsync<IReadOnlyList<ProcessingJobSnapshot>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id
                FROM processing_jobs
                WHERE state NOT IN ($completed, $cancelled)
                  AND class_id IS NOT NULL
                  AND mp4_path IS NOT NULL
                  AND job_directory IS NOT NULL
                  AND created_at IS NOT NULL
                ORDER BY updated_at, id;
                """;
            command.Parameters.AddWithValue("$completed", StateText(ProcessingState.Completed));
            command.Parameters.AddWithValue("$cancelled", StateText(ProcessingState.Cancelled));
            await using var reader = await command.ExecuteReaderAsync(token);
            var ids = new List<Guid>();
            while (await reader.ReadAsync(token))
            {
                ids.Add(ParseGuid(reader.GetString(0), "processing job id"));
            }

            await reader.DisposeAsync();
            var result = new List<ProcessingJobSnapshot>(ids.Count);
            foreach (var id in ids)
            {
                result.Add(await ReadJobAsync(connection, transaction: null, id, token));
            }

            return result;
        }, cancellationToken);

    private static async Task<ProcessingJobSnapshot> ReadJobAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT jobs.id, jobs.recording_id, jobs.class_id, jobs.mp4_path, jobs.job_directory,
                   jobs.delete_video, jobs.state, jobs.failed_stage, jobs.error_code,
                   jobs.transcript_committed, jobs.lecture_package_committed,
                   jobs.assignments_committed, jobs.guide_outcome, jobs.revision, jobs.updated_at,
                   transcripts.artifact_path, transcripts.artifact_sha256,
                   packages.artifact_path, packages.artifact_sha256
            FROM processing_jobs jobs
            LEFT JOIN processing_transcripts transcripts ON transcripts.job_id = jobs.id
            LEFT JOIN lecture_packages packages ON packages.recording_id = jobs.recording_id
            WHERE jobs.id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", GuidText(jobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("The processing job does not exist.");
        }

        if (reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(14))
        {
            throw new InvalidDataException("The legacy processing job does not contain a resumable request.");
        }

        var transcriptCommitted = ReadBoolean(reader, 9);
        var packageCommitted = ReadBoolean(reader, 10);
        return new ProcessingJobSnapshot(
            new ProcessingRequest(
                ParseGuid(reader.GetString(0), "processing job id"),
                ParseGuid(reader.GetString(1), "recording id"),
                ParseGuid(reader.GetString(2), "class id"),
                reader.GetString(3),
                reader.GetString(4),
                ReadBoolean(reader, 5)),
            ParseState(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseState(reader.GetString(7)),
            reader.IsDBNull(8) ? null : ParseErrorCode(reader.GetString(8)),
            transcriptCommitted,
            transcriptCommitted && !reader.IsDBNull(15) && !reader.IsDBNull(16)
                ? new ArtifactCheckpoint(reader.GetString(15), reader.GetString(16))
                : null,
            packageCommitted,
            packageCommitted && !reader.IsDBNull(17) && !reader.IsDBNull(18)
                ? new ArtifactCheckpoint(reader.GetString(17), reader.GetString(18))
                : null,
            ReadBoolean(reader, 11),
            ParseGuideOutcome(reader.GetString(12)),
            reader.GetInt64(13),
            ParseTimestamp(reader.GetString(14)));
    }

    private static async Task<ProcessingJobSnapshot> ReadExpectedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        long expectedRevision,
        ProcessingState expectedState,
        CancellationToken cancellationToken)
    {
        var current = await ReadJobAsync(connection, transaction, jobId, cancellationToken);
        AssertRevision(current, expectedRevision);
        if (current.State != expectedState)
        {
            throw new ProcessingConcurrencyException(jobId);
        }

        return current;
    }

    private static async Task ValidateForwardMoveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProcessingJobSnapshot current,
        ProcessingState nextState,
        CancellationToken cancellationToken)
    {
        var legal = current.State switch
        {
            ProcessingState.ReadyToProcess => nextState == ProcessingState.PreparingAudio,
            ProcessingState.PreparingAudio =>
                nextState == ProcessingState.Transcribing &&
                await HasRowsAsync(connection, transaction, "audio_chunks", current.Request.JobId, cancellationToken),
            ProcessingState.Transcribing =>
                nextState == ProcessingState.GeneratingStudyPackage && current.TranscriptCommitted,
            ProcessingState.GeneratingStudyPackage =>
                nextState == ProcessingState.UpdatingClassGuide &&
                current.LecturePackageCommitted && current.AssignmentsCommitted,
            _ => false
        };
        if (!legal)
        {
            throw new InvalidProcessingTransitionException(current.State, nextState);
        }
    }

    private static async Task<bool> HasRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE job_id = $jobId);";
        command.Parameters.AddWithValue("$jobId", GuidText(jobId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private async Task BumpRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProcessingJobSnapshot current,
        string assignments,
        Action<SqliteCommand>? parameters,
        CancellationToken cancellationToken)
    {
        await UpdateStateAsync(
            connection,
            transaction,
            current.Request.JobId,
            current.Revision,
            current.State,
            assignments,
            parameters,
            cancellationToken);
    }

    private async Task UpdateStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        long expectedRevision,
        ProcessingState expectedState,
        string assignments,
        Action<SqliteCommand>? parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE processing_jobs
            SET {assignments}, revision = revision + 1, updated_at = $updatedAt
            WHERE id = $jobId AND revision = $expectedRevision AND state = $expectedState;
            """;
        command.Parameters.AddWithValue("$updatedAt", TimestampText(utcNow()));
        command.Parameters.AddWithValue("$jobId", GuidText(jobId));
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        command.Parameters.AddWithValue("$expectedState", StateText(expectedState));
        parameters?.Invoke(command);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ProcessingConcurrencyException(jobId);
        }
    }

    private static async Task InsertAudioChunkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        AudioChunk chunk,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audio_chunks(
                job_id, chunk_index, start_ms, end_ms, sha256, artifact_path, byte_size)
            VALUES ($jobId, $index, $start, $end, $hash, $path, $byteSize);
            """;
        command.Parameters.AddWithValue("$jobId", GuidText(jobId));
        command.Parameters.AddWithValue("$index", chunk.Index);
        command.Parameters.AddWithValue("$start", chunk.StartMilliseconds);
        command.Parameters.AddWithValue("$end", chunk.EndMilliseconds);
        command.Parameters.AddWithValue("$hash", chunk.Sha256);
        command.Parameters.AddWithValue("$path", chunk.Path);
        command.Parameters.AddWithValue("$byteSize", chunk.ByteSize);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<int, TranscriptChunkCheckpoint>> ReadReusableTranscriptChunksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT chunk_index, audio_sha256, artifact_path, artifact_sha256
            FROM transcription_chunks WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", GuidText(jobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<int, TranscriptChunkCheckpoint>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var index = reader.GetInt32(0);
            result[index] = new TranscriptChunkCheckpoint(
                index,
                reader.GetString(1),
                new ArtifactCheckpoint(reader.GetString(2), reader.GetString(3)));
        }

        return result;
    }

    private static async Task UpsertTranscriptChunkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        TranscriptChunkCheckpoint chunk,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO transcription_chunks(
                job_id, chunk_index, audio_sha256, artifact_path, artifact_sha256)
            VALUES ($jobId, $index, $audioHash, $path, $artifactHash)
            ON CONFLICT(job_id, chunk_index) DO UPDATE SET
                audio_sha256 = excluded.audio_sha256,
                artifact_path = excluded.artifact_path,
                artifact_sha256 = excluded.artifact_sha256;
            """;
        command.Parameters.AddWithValue("$jobId", GuidText(jobId));
        command.Parameters.AddWithValue("$index", chunk.Index);
        command.Parameters.AddWithValue("$audioHash", chunk.AudioSha256);
        command.Parameters.AddWithValue("$path", chunk.Artifact.Path);
        command.Parameters.AddWithValue("$artifactHash", chunk.Artifact.Sha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureAudioHashAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        int index,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM audio_chunks
                WHERE job_id = $jobId AND chunk_index = $index AND sha256 = $hash);
            """;
        command.Parameters.AddWithValue("$jobId", GuidText(jobId));
        command.Parameters.AddWithValue("$index", index);
        command.Parameters.AddWithValue("$hash", hash);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidDataException("The transcript result does not match a stored audio chunk.");
        }
    }

    private static async Task InsertAssignmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordingId,
        StudyAssignment assignment,
        int index,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO assignments(
                id, recording_id, description, due_date_text, due_at, confidence,
                is_user_confirmed, source_timestamp_ms, source_order)
            VALUES ($id, $recordingId, $description, $dueDateText, $dueAt, $confidence, 0, $sourceTimestamp, $sourceOrder);
            """;
        command.Parameters.AddWithValue("$id", GuidText(Guid.NewGuid()));
        command.Parameters.AddWithValue("$recordingId", GuidText(recordingId));
        command.Parameters.AddWithValue("$description", assignment.Description);
        command.Parameters.AddWithValue("$dueDateText", assignment.DueDateText);
        command.Parameters.AddWithValue("$dueAt", assignment.NormalizedDueDate is { } date
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DBNull.Value);
        command.Parameters.AddWithValue("$confidence", assignment.Confidence);
        command.Parameters.AddWithValue("$sourceTimestamp", assignment.SourceTimestamp.StartMilliseconds);
        command.Parameters.AddWithValue("$sourceOrder", index);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureJobExistsAsync(
        SqliteConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM processing_jobs WHERE id = $jobId);";
        command.Parameters.AddWithValue("$jobId", GuidText(jobId));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
        {
            throw new KeyNotFoundException("The processing job does not exist.");
        }
    }

    private async Task<T> WithConnectionAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await database.Gate.WaitAsync(cancellationToken);
        try
        {
            return await action(database.Connection, cancellationToken);
        }
        finally
        {
            database.Gate.Release();
        }
    }

    private async Task<T> WithTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await database.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction =
                (SqliteTransaction)await database.Connection.BeginTransactionAsync(cancellationToken);
            var result = await action(database.Connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            database.Gate.Release();
        }
    }

    private static AudioChunk[] ValidateAudioChunks(IReadOnlyList<AudioChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0 || chunks.Any(item => item is null))
        {
            throw new ArgumentException("At least one audio chunk is required.", nameof(chunks));
        }

        var ordered = chunks.OrderBy(item => item.Index).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var chunk = ordered[index];
            if (chunk.Index != index || chunk.StartMilliseconds < 0 ||
                chunk.EndMilliseconds <= chunk.StartMilliseconds || chunk.ByteSize <= 0)
            {
                throw new ArgumentException("Audio chunks must be contiguous and valid.", nameof(chunks));
            }

            ValidatePath(chunk.Path, nameof(chunks));
            ValidateHash(chunk.Sha256, nameof(chunks));
        }

        return ordered;
    }

    private static void ValidateAudioChunkLocations(
        IReadOnlyList<AudioChunk> chunks,
        string jobDirectory)
    {
        var expectedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobDirectory));
        foreach (var chunk in chunks)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(chunk.Path));
            if (!string.Equals(parent, expectedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Audio chunks must be direct children of the registered job directory.",
                    nameof(chunks));
            }
        }
    }

    private static void ValidateTranscriptChunk(TranscriptChunkCheckpoint chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk));
        }

        ValidateHash(chunk.AudioSha256, nameof(chunk));
        ValidateArtifact(chunk.Artifact);
    }

    private static void ValidateAssignments(IReadOnlyList<StudyAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        foreach (var assignment in assignments)
        {
            if (assignment is null || string.IsNullOrWhiteSpace(assignment.Description) ||
                string.IsNullOrWhiteSpace(assignment.DueDateText) ||
                !double.IsFinite(assignment.Confidence) || assignment.Confidence is < 0 or > 1 ||
                assignment.SourceTimestamp is null ||
                assignment.SourceTimestamp.StartMilliseconds < 0 ||
                assignment.SourceTimestamp.EndMilliseconds < assignment.SourceTimestamp.StartMilliseconds)
            {
                throw new ArgumentException("Assignments contain invalid values.", nameof(assignments));
            }
        }
    }

    private static void ValidateArtifact(ArtifactCheckpoint artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidatePath(artifact.Path, nameof(artifact));
        ValidateHash(artifact.Sha256, nameof(artifact));
    }

    private static void ValidateRequest(ProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateId(request.JobId, nameof(request.JobId));
        ValidateId(request.RecordingId, nameof(request.RecordingId));
        ValidateId(request.ClassId, nameof(request.ClassId));
        ValidatePath(request.Mp4Path, nameof(request.Mp4Path));
        ValidatePath(request.JobDirectory, nameof(request.JobDirectory));
    }

    private static void ValidatePath(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, name);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be fully qualified.", name);
        }
    }

    private static void ValidateHash(string hash, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash, name);
        if (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 value is required.", name);
        }
    }

    private static void ValidateRevision(long revision)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }
    }

    private static void ValidateId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identifier is required.", name);
        }
    }

    private static void AssertRevision(ProcessingJobSnapshot current, long expectedRevision)
    {
        ValidateRevision(expectedRevision);
        if (current.Revision != expectedRevision)
        {
            throw new ProcessingConcurrencyException(current.Request.JobId);
        }
    }

    private static bool IsActive(ProcessingState state) => state is
        ProcessingState.ReadyToProcess or
        ProcessingState.PreparingAudio or
        ProcessingState.Transcribing or
        ProcessingState.GeneratingStudyPackage or
        ProcessingState.UpdatingClassGuide;

    private static string StateText(ProcessingState state) =>
        Enum.IsDefined(state) ? state.ToString() : throw new ArgumentOutOfRangeException(nameof(state));

    private static ProcessingState ParseState(string value) =>
        Enum.TryParse<ProcessingState>(value, ignoreCase: false, out var state) && Enum.IsDefined(state)
            ? state
            : throw new InvalidDataException("The processing job contains an unknown state.");

    private static CloudProcessingErrorCode ParseErrorCode(string value) =>
        Enum.TryParse<CloudProcessingErrorCode>(value, ignoreCase: false, out var code) && Enum.IsDefined(code)
            ? code
            : throw new InvalidDataException("The processing job contains an unknown error code.");

    private static string GuideOutcomeText(ClassGuideOutcome outcome) =>
        Enum.IsDefined(outcome) ? outcome.ToString() : throw new ArgumentOutOfRangeException(nameof(outcome));

    private static ClassGuideOutcome ParseGuideOutcome(string value) =>
        Enum.TryParse<ClassGuideOutcome>(value, ignoreCase: false, out var outcome) && Enum.IsDefined(outcome)
            ? outcome
            : throw new InvalidDataException("The processing job contains an unknown guide outcome.");

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal) => reader.GetInt64(ordinal) switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("The database contains an invalid Boolean value.")
    };

    private static int BooleanInteger(bool value) => value ? 1 : 0;
    private static string GuidText(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
    private static Guid ParseGuid(string value, string name) =>
        Guid.TryParseExact(value, "D", out var result)
            ? result
            : throw new InvalidDataException($"The database contains an invalid {name}.");
    private static string TimestampText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result
            : throw new InvalidDataException("The database contains an invalid processing timestamp.");
}
