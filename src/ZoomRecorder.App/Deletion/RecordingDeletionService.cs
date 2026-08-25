using Microsoft.Data.Sqlite;
using ZoomRecorder.App.Data;

namespace ZoomRecorder.App.Deletion;

public sealed class RecordingDeletionService
{
    private readonly LibraryDatabase database;
    private readonly LibraryPaths paths;

    public RecordingDeletionService(LibraryDatabase database, LibraryPaths paths)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task DeleteAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = await LoadPlanAsync(recordingId, cancellationToken);
        var targets = ValidateAndCollectTargets(plan);
        Preflight(targets, cancellationToken);
        DeleteFiles(targets, cancellationToken);
        await DeleteDatabaseRowsAsync(plan, cancellationToken);
    }

    private async Task<DeletionPlan> LoadPlanAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        await database.Gate.WaitAsync(cancellationToken);
        try
        {
            Guid? classId;
            string videoPath;
            await using (var recording = database.Connection.CreateCommand())
            {
                recording.CommandText = "SELECT class_id, file_path FROM recordings WHERE id = $recordingId;";
                recording.Parameters.AddWithValue("$recordingId", GuidText(recordingId));
                await using var reader = await recording.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new KeyNotFoundException("The recording does not exist.");
                }

                classId = reader.IsDBNull(0) ? null : Guid.ParseExact(reader.GetString(0), "D");
                videoPath = reader.GetString(1);
            }

            var jobDirectories = new List<string>();
            await using (var jobs = database.Connection.CreateCommand())
            {
                jobs.CommandText = "SELECT job_directory, state FROM processing_jobs WHERE recording_id = $recordingId;";
                jobs.Parameters.AddWithValue("$recordingId", GuidText(recordingId));
                await using var reader = await jobs.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var state = reader.GetString(1);
                    if (state is not ("Completed" or "Cancelled"))
                    {
                        throw new InvalidOperationException("The recording is still being processed.");
                    }

                    jobDirectories.Add(reader.GetString(0));
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

            return new DeletionPlan(recordingId, classId, videoPath, jobDirectories, classGuidePath);
        }
        finally
        {
            database.Gate.Release();
        }
    }

    private DeletionTargets ValidateAndCollectTargets(DeletionPlan plan)
    {
        var videoPath = ValidateFilePath(plan.VideoPath);
        var recordingArtifacts = ValidateChildDirectory(
            paths.ArtifactsRoot,
            Path.Combine(paths.ArtifactsRoot, GuidText(plan.RecordingId)));
        var jobDirectories = plan.JobDirectories
            .Select(path => ValidateChildDirectory(paths.JobsRoot, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    private static void Preflight(DeletionTargets targets, CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFile(files, targets.VideoPath);
        AddFile(files, targets.ClassGuidePath);
        AddDirectoryFiles(files, targets.RecordingArtifacts);
        foreach (var directory in targets.JobDirectories)
        {
            AddDirectoryFiles(files, directory);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        }
    }

    private static void DeleteFiles(DeletionTargets targets, CancellationToken cancellationToken)
    {
        DeleteFile(targets.VideoPath, cancellationToken);
        DeleteFile(targets.ClassGuidePath, cancellationToken);
        foreach (var directory in targets.JobDirectories)
        {
            DeleteDirectory(directory, cancellationToken);
        }

        DeleteDirectory(targets.RecordingArtifacts, cancellationToken);
    }

    private async Task DeleteDatabaseRowsAsync(DeletionPlan plan, CancellationToken cancellationToken)
    {
        await database.Gate.WaitAsync(cancellationToken);
        try
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
        finally
        {
            database.Gate.Release();
        }
    }

    private static void AddFile(ISet<string> files, string? path)
    {
        if (path is not null && File.Exists(path))
        {
            files.Add(path);
        }
    }

    private static void AddDirectoryFiles(ISet<string> files, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            files.Add(Path.GetFullPath(file));
        }
    }

    private static void DeleteFile(string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectory(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string ValidateFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.GetFullPath(path);
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(canonical)) is null)
        {
            throw new InvalidDataException("A file-system root cannot be deleted.");
        }

        return canonical;
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
        IReadOnlyList<string> JobDirectories,
        string? ClassGuidePath);

    private sealed record DeletionTargets(
        string VideoPath,
        string RecordingArtifacts,
        IReadOnlyList<string> JobDirectories,
        string? ClassGuidePath);
}
