using Microsoft.Data.Sqlite;
using ZoomRecorder.App.Data;

namespace ZoomRecorder.App.Deletion;

internal interface IRecordingDeletionFileSystem
{
    Task<IStagedRecordingDeletion> StageAsync(
        Guid recordingId,
        string videoPath,
        string recordingArtifacts,
        IReadOnlyList<string> jobDirectories,
        string? classGuidePath,
        CancellationToken cancellationToken);
}

internal interface IStagedRecordingDeletion
{
    void Rollback();
    void Purge();
}

internal interface IRecordingDeletionFileOperations
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void MoveFile(string source, string destination);
    void MoveDirectory(string source, string destination);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
}

public sealed class RecordingDeletionService
{
    private readonly LibraryDatabase database;
    private readonly LibraryPaths paths;
    private readonly IRecordingDeletionFileSystem fileSystem;

    public RecordingDeletionService(LibraryDatabase database, LibraryPaths paths)
        : this(database, paths, new PhysicalRecordingDeletionFileSystem())
    {
    }

    internal RecordingDeletionService(
        LibraryDatabase database,
        LibraryPaths paths,
        IRecordingDeletionFileSystem fileSystem)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public async Task DeleteAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await database.Gate.WaitAsync(cancellationToken);
        try
        {
            var plan = await LoadPlanAsync(recordingId, cancellationToken);
            var targets = ValidateAndCollectTargets(plan);
            var staged = await fileSystem.StageAsync(
                recordingId,
                targets.VideoPath,
                targets.RecordingArtifacts,
                targets.JobDirectories,
                targets.ClassGuidePath,
                cancellationToken);
            try
            {
                await DeleteDatabaseRowsAsync(plan, CancellationToken.None);
            }
            catch (Exception databaseFailure)
            {
                try
                {
                    staged.Rollback();
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(databaseFailure, rollbackFailure);
                }

                throw;
            }

            staged.Purge();
        }
        finally
        {
            database.Gate.Release();
        }
    }

    private async Task<DeletionPlan> LoadPlanAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        Guid? classId;
        string videoPath;
        string fileName;
        await using (var recording = database.Connection.CreateCommand())
        {
            recording.CommandText = "SELECT class_id, file_path, file_name FROM recordings WHERE id = $recordingId;";
            recording.Parameters.AddWithValue("$recordingId", GuidText(recordingId));
            await using var reader = await recording.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException("The recording does not exist.");
            }

            classId = reader.IsDBNull(0) ? null : Guid.ParseExact(reader.GetString(0), "D");
            videoPath = reader.GetString(1);
            fileName = reader.GetString(2);
        }

        var jobDirectories = new List<JobDirectory>();
        await using (var jobs = database.Connection.CreateCommand())
        {
            jobs.CommandText = "SELECT id, job_directory, state FROM processing_jobs WHERE recording_id = $recordingId;";
            jobs.Parameters.AddWithValue("$recordingId", GuidText(recordingId));
            await using var reader = await jobs.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var state = reader.GetString(2);
                if (state is not ("Completed" or "Cancelled"))
                {
                    throw new InvalidOperationException("The recording is still being processed.");
                }

                jobDirectories.Add(new JobDirectory(
                    Guid.ParseExact(reader.GetString(0), "D"),
                    reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        string? classGuidePath = null;
        if (classId is { } assignedClassId)
        {
            await using var guide = database.Connection.CreateCommand();
            guide.CommandText = "SELECT artifact_path FROM class_study_guides WHERE class_id = $classId;";
            guide.Parameters.AddWithValue("$classId", GuidText(assignedClassId));
            classGuidePath = await guide.ExecuteScalarAsync(cancellationToken) as string;
        }

        var artifactPaths = new List<PersistedArtifactPath>();
        await using (var artifacts = database.Connection.CreateCommand())
        {
            artifacts.CommandText = """
                SELECT 'job', jobs.id, chunks.artifact_path
                FROM audio_chunks chunks
                JOIN processing_jobs jobs ON jobs.id = chunks.job_id
                WHERE jobs.recording_id = $recordingId AND chunks.artifact_path IS NOT NULL
                UNION ALL
                SELECT 'job', jobs.id, chunks.artifact_path
                FROM transcription_chunks chunks
                JOIN processing_jobs jobs ON jobs.id = chunks.job_id
                WHERE jobs.recording_id = $recordingId AND chunks.artifact_path IS NOT NULL
                UNION ALL
                SELECT 'job', jobs.id, transcripts.artifact_path
                FROM processing_transcripts transcripts
                JOIN processing_jobs jobs ON jobs.id = transcripts.job_id
                WHERE jobs.recording_id = $recordingId
                UNION ALL
                SELECT 'recording', $recordingId, packages.artifact_path
                FROM lecture_packages packages
                WHERE packages.recording_id = $recordingId;
                """;
            artifacts.Parameters.AddWithValue("$recordingId", GuidText(recordingId));
            await using var reader = await artifacts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                artifactPaths.Add(new PersistedArtifactPath(
                    reader.GetString(0) == "job" ? ArtifactOwner.Job : ArtifactOwner.Recording,
                    Guid.ParseExact(reader.GetString(1), "D"),
                    reader.GetString(2)));
            }
        }

        return new DeletionPlan(
            recordingId, classId, videoPath, fileName, jobDirectories, artifactPaths, classGuidePath);
    }

    private DeletionTargets ValidateAndCollectTargets(DeletionPlan plan)
    {
        var videoPath = ValidateFileWithinDirectory(paths.RecordingsRoot, plan.VideoPath);
        if (!string.Equals(Path.GetExtension(videoPath), ".mp4", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(videoPath), plan.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The recording file metadata is inconsistent.");
        }
        var recordingArtifacts = ValidateChildDirectory(
            paths.ArtifactsRoot,
            Path.Combine(paths.ArtifactsRoot, GuidText(plan.RecordingId)));
        var jobDirectories = plan.JobDirectories
            .Select(job =>
            {
                var expected = ValidateChildDirectory(
                    paths.JobsRoot,
                    Path.Combine(paths.JobsRoot, GuidText(job.JobId)));
                var stored = job.Path is null
                    ? expected
                    : ValidateChildDirectory(paths.JobsRoot, job.Path);
                if (!string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A processing directory does not match its job owner.");
                }

                return expected;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var artifact in plan.ArtifactPaths)
        {
            var expectedDirectory = artifact.Owner switch
            {
                ArtifactOwner.Job => ValidateChildDirectory(
                    paths.JobsRoot,
                    Path.Combine(paths.JobsRoot, GuidText(artifact.OwnerId))),
                ArtifactOwner.Recording when artifact.OwnerId == plan.RecordingId => recordingArtifacts,
                _ => throw new InvalidDataException("An artifact does not match its recording owner.")
            };
            ValidateFileWithinDirectory(expectedDirectory, artifact.Path);
        }

        string? classGuidePath = null;
        if (!string.IsNullOrWhiteSpace(plan.ClassGuidePath) && plan.ClassId is { } classId)
        {
            var classDirectory = ValidateChildDirectory(
                paths.ArtifactsRoot,
                Path.Combine(paths.ArtifactsRoot, GuidText(classId)));
            classGuidePath = ValidateFileWithinDirectory(classDirectory, plan.ClassGuidePath);
        }

        return new DeletionTargets(videoPath, recordingArtifacts, jobDirectories, classGuidePath);
    }

    private async Task DeleteDatabaseRowsAsync(DeletionPlan plan, CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await database.Connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                    DELETE FROM transcription_chunks WHERE job_id IN (
                        SELECT id FROM processing_jobs WHERE recording_id = $recordingId);
                    DELETE FROM processing_transcripts WHERE job_id IN (
                        SELECT id FROM processing_jobs WHERE recording_id = $recordingId);
                    DELETE FROM audio_chunks WHERE job_id IN (
                        SELECT id FROM processing_jobs WHERE recording_id = $recordingId);
                    DELETE FROM processing_jobs WHERE recording_id = $recordingId;
                    DELETE FROM assignments WHERE recording_id = $recordingId;
                    DELETE FROM lecture_packages WHERE recording_id = $recordingId;
                    DELETE FROM class_study_guides WHERE class_id = $classId;
                    DELETE FROM recordings WHERE id = $recordingId;
                    """;
            command.Parameters.AddWithValue("$recordingId", GuidText(plan.RecordingId));
            command.Parameters.AddWithValue("$classId", plan.ClassId is { } classId
                ? GuidText(classId)
                : DBNull.Value);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                throw new KeyNotFoundException("The recording does not exist.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static string ValidateChildDirectory(string root, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (string.Equals(canonical, canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
            !canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A deletion directory is outside the application data root.");
        }

        return canonical;
    }

    private static string ValidateFileWithinDirectory(string directory, string path)
    {
        var canonical = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        if (!canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A deletion file is outside its application data directory.");
        }

        return canonical;
    }

    private static string GuidText(Guid value) => value.ToString("D");

    private sealed record DeletionPlan(
        Guid RecordingId,
        Guid? ClassId,
        string VideoPath,
        string FileName,
        IReadOnlyList<JobDirectory> JobDirectories,
        IReadOnlyList<PersistedArtifactPath> ArtifactPaths,
        string? ClassGuidePath);

    private sealed record JobDirectory(Guid JobId, string? Path);

    private sealed record PersistedArtifactPath(ArtifactOwner Owner, Guid OwnerId, string Path);

    private enum ArtifactOwner
    {
        Job,
        Recording
    }

    private sealed record DeletionTargets(
        string VideoPath,
        string RecordingArtifacts,
        IReadOnlyList<string> JobDirectories,
        string? ClassGuidePath);

}

internal sealed class PhysicalRecordingDeletionFileSystem : IRecordingDeletionFileSystem
{
    private readonly IRecordingDeletionFileOperations operations;

    public PhysicalRecordingDeletionFileSystem()
        : this(new SystemRecordingDeletionFileOperations())
    {
    }

    internal PhysicalRecordingDeletionFileSystem(IRecordingDeletionFileOperations operations)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public Task<IStagedRecordingDeletion> StageAsync(
        Guid recordingId,
        string videoPath,
        string recordingArtifacts,
        IReadOnlyList<string> jobDirectories,
        string? classGuidePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<StagedEntry>();
        try
        {
            StageFile(videoPath, recordingId, entries);
            if (classGuidePath is not null)
            {
                StageFile(classGuidePath, recordingId, entries);
            }

            foreach (var jobDirectory in jobDirectories)
            {
                StageDirectory(jobDirectory, recordingId, entries);
            }

            StageDirectory(recordingArtifacts, recordingId, entries);
            return Task.FromResult<IStagedRecordingDeletion>(
                new StagedRecordingDeletion(operations, entries));
        }
        catch (Exception stagingFailure)
        {
            try
            {
                StagedRecordingDeletion.Rollback(operations, entries);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(stagingFailure, rollbackFailure);
            }

            throw;
        }
    }

    private void StageFile(string source, Guid recordingId, ICollection<StagedEntry> entries)
    {
        if (!operations.FileExists(source))
        {
            return;
        }

        var destination = RecordingDeletionQuarantine.PathFor(source, recordingId);
        operations.MoveFile(source, destination);
        entries.Add(new StagedEntry(source, destination, IsDirectory: false));
    }

    private void StageDirectory(string source, Guid recordingId, ICollection<StagedEntry> entries)
    {
        if (!operations.DirectoryExists(source))
        {
            return;
        }

        var destination = RecordingDeletionQuarantine.PathFor(source, recordingId);
        operations.MoveDirectory(source, destination);
        entries.Add(new StagedEntry(source, destination, IsDirectory: true));
    }

    internal sealed record StagedEntry(string Original, string Quarantine, bool IsDirectory);

    private sealed class StagedRecordingDeletion(
        IRecordingDeletionFileOperations operations,
        IReadOnlyList<StagedEntry> entries) : IStagedRecordingDeletion
    {
        public void Rollback() => Rollback(operations, entries);

        public void Purge()
        {
            foreach (var entry in entries.Reverse())
            {
                try
                {
                    if (entry.IsDirectory && operations.DirectoryExists(entry.Quarantine))
                    {
                        operations.DeleteDirectory(entry.Quarantine);
                    }
                    else if (!entry.IsDirectory && operations.FileExists(entry.Quarantine))
                    {
                        operations.DeleteFile(entry.Quarantine);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        internal static void Rollback(
            IRecordingDeletionFileOperations operations,
            IReadOnlyList<StagedEntry> entries)
        {
            var failures = new List<Exception>();
            foreach (var entry in entries.Reverse())
            {
                try
                {
                    if (entry.IsDirectory)
                    {
                        operations.MoveDirectory(entry.Quarantine, entry.Original);
                    }
                    else
                    {
                        operations.MoveFile(entry.Quarantine, entry.Original);
                    }
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("One or more staged recording files could not be restored.", failures);
            }
        }
    }

    private sealed class SystemRecordingDeletionFileOperations : IRecordingDeletionFileOperations
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void MoveFile(string source, string destination) => File.Move(source, destination);
        public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
    }
}
