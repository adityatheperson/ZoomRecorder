using System.Globalization;
using Microsoft.Data.Sqlite;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Data;

public sealed class SqliteLibraryRepository : ILibraryRepository
{
    private const string RecordingColumns =
        "id, class_id, file_path, file_name, meeting_id, recorded_at, duration_ms, byte_size, video_available";

    private readonly LibraryDatabase _database;
    private readonly Func<DateTimeOffset> _utcNow;

    public SqliteLibraryRepository(LibraryDatabase database)
        : this(database, () => DateTimeOffset.UtcNow)
    {
    }

    internal SqliteLibraryRepository(LibraryDatabase database, Func<DateTimeOffset> utcNow)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public Task<ClassRecord> CreateClassAsync(
        string name,
        string? term,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return WithConnectionAsync(async connection =>
        {
            var classRecord = new ClassRecord(Guid.NewGuid(), name, term, _utcNow(), false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO classes(id, name, term, created_at, is_archived)
                VALUES ($id, $name, $term, $createdAt, $isArchived);
                """;
            command.Parameters.AddWithValue("$id", GuidText(classRecord.Id));
            command.Parameters.AddWithValue("$name", classRecord.Name);
            command.Parameters.AddWithValue("$term", DbValue(classRecord.Term));
            command.Parameters.AddWithValue("$createdAt", TimestampText(classRecord.CreatedAt));
            command.Parameters.AddWithValue("$isArchived", BooleanInteger(classRecord.IsArchived));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return classRecord;
        }, cancellationToken);
    }

    public Task<RecordingRecord> AddRecordingAsync(
        RecordingRecord recording,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);

        return WithConnectionAsync(async connection =>
        {
            var canonicalRecording = recording with { FilePath = Path.GetFullPath(recording.FilePath) };
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO recordings(
                    id, class_id, file_path, file_name, meeting_id, recorded_at,
                    duration_ms, byte_size, video_available)
                VALUES (
                    $id, $classId, $filePath, $fileName, $meetingId, $recordedAt,
                    $durationMs, $byteSize, $videoAvailable);
                """;
            command.Parameters.AddWithValue("$id", GuidText(canonicalRecording.Id));
            command.Parameters.AddWithValue("$classId", DbGuid(canonicalRecording.ClassId));
            command.Parameters.AddWithValue("$filePath", canonicalRecording.FilePath);
            command.Parameters.AddWithValue("$fileName", canonicalRecording.FileName);
            command.Parameters.AddWithValue("$meetingId", DbValue(canonicalRecording.MeetingId));
            command.Parameters.AddWithValue("$recordedAt", TimestampText(canonicalRecording.RecordedAt));
            command.Parameters.AddWithValue("$durationMs", checked((long)canonicalRecording.Duration.TotalMilliseconds));
            command.Parameters.AddWithValue("$byteSize", canonicalRecording.ByteSize);
            command.Parameters.AddWithValue("$videoAvailable", BooleanInteger(canonicalRecording.VideoAvailable));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return canonicalRecording;
        }, cancellationToken);
    }

    public Task<RecordingRecord?> FindRecordingByPathAsync(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        var fullPath = Path.GetFullPath(canonicalPath);

        return WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {RecordingColumns} FROM recordings WHERE file_path = $filePath;";
            command.Parameters.AddWithValue("$filePath", fullPath);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadRecording(reader) : null;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ClassRecord>> ListClassesAsync(
        bool includeArchived,
        CancellationToken cancellationToken) =>
        WithConnectionAsync<IReadOnlyList<ClassRecord>>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, term, created_at, is_archived
                FROM classes
                WHERE $includeArchived = 1 OR is_archived = 0;
                """;
            command.Parameters.AddWithValue("$includeArchived", BooleanInteger(includeArchived));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var classes = new List<ClassRecord>();
            while (await reader.ReadAsync(cancellationToken))
            {
                classes.Add(ReadClass(reader));
            }

            return classes
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Id)
                .ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<RecordingRecord>> ListRecordingsAsync(
        Guid? classId,
        CancellationToken cancellationToken)
    {
        if (classId is not Guid assignedClassId)
        {
            return ReadRecordingsAsync(null, null, cancellationToken);
        }

        return ReadRecordingsAsync(
            "class_id = $classId",
            command => command.Parameters.AddWithValue("$classId", GuidText(assignedClassId)),
            cancellationToken);
    }

    public Task<IReadOnlyList<RecordingRecord>> ListUnassignedRecordingsAsync(
        CancellationToken cancellationToken) =>
        ReadRecordingsAsync("class_id IS NULL", null, cancellationToken);

    public Task<IReadOnlyList<RecordingRecord>> SearchClassRecordingsAsync(
        Guid classId,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return ReadRecordingsAsync(
            "class_id = $classId AND instr(lower(file_name), lower($query)) > 0",
            command =>
            {
                command.Parameters.AddWithValue("$classId", GuidText(classId));
                command.Parameters.AddWithValue("$query", query);
            },
            cancellationToken);
    }

    public Task AssignRecordingAsync(
        Guid recordingId,
        Guid? classId,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE recordings SET class_id = $classId WHERE id = $recordingId;";
            command.Parameters.AddWithValue("$classId", DbGuid(classId));
            command.Parameters.AddWithValue("$recordingId", GuidText(recordingId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<MeetingClassMapping?> FindMappingAsync(
        string meetingId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meetingId);

        return WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT meeting_id, class_id FROM meeting_class_mappings WHERE meeting_id = $meetingId;";
            command.Parameters.AddWithValue("$meetingId", meetingId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new MeetingClassMapping(reader.GetString(0), ParseGuid(reader.GetString(1)))
                : null;
        }, cancellationToken);
    }

    public Task UpsertMappingAsync(
        MeetingClassMapping mapping,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        return WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO meeting_class_mappings(meeting_id, class_id)
                VALUES ($meetingId, $classId)
                ON CONFLICT(meeting_id) DO UPDATE SET class_id = excluded.class_id;
                """;
            command.Parameters.AddWithValue("$meetingId", mapping.MeetingId);
            command.Parameters.AddWithValue("$classId", GuidText(mapping.ClassId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task ForgetMappingAsync(string meetingId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meetingId);

        return WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM meeting_class_mappings WHERE meeting_id = $meetingId;";
            command.Parameters.AddWithValue("$meetingId", meetingId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    private Task<IReadOnlyList<RecordingRecord>> ReadRecordingsAsync(
        string? predicate,
        Action<SqliteCommand>? addParameters,
        CancellationToken cancellationToken) =>
        WithConnectionAsync<IReadOnlyList<RecordingRecord>>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = predicate is null
                ? $"SELECT {RecordingColumns} FROM recordings;"
                : $"SELECT {RecordingColumns} FROM recordings WHERE {predicate};";
            addParameters?.Invoke(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var recordings = new List<RecordingRecord>();
            while (await reader.ReadAsync(cancellationToken))
            {
                recordings.Add(ReadRecording(reader));
            }

            return recordings
                .OrderByDescending(item => item.RecordedAt)
                .ThenBy(item => item.Id)
                .ToArray();
        }, cancellationToken);

    private async Task<T> WithConnectionAsync<T>(
        Func<SqliteConnection, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _database.Gate.WaitAsync(cancellationToken);
        try
        {
            return await operation(_database.Connection);
        }
        finally
        {
            _database.Gate.Release();
        }
    }

    private async Task WithConnectionAsync(
        Func<SqliteConnection, Task> operation,
        CancellationToken cancellationToken)
    {
        await _database.Gate.WaitAsync(cancellationToken);
        try
        {
            await operation(_database.Connection);
        }
        finally
        {
            _database.Gate.Release();
        }
    }

    private static ClassRecord ReadClass(SqliteDataReader reader) =>
        new(
            ParseGuid(reader.GetString(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            reader.GetInt64(4) != 0);

    private static RecordingRecord ReadRecording(SqliteDataReader reader) =>
        new(
            ParseGuid(reader.GetString(0)),
            reader.IsDBNull(1) ? null : ParseGuid(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            TimeSpan.FromMilliseconds(reader.GetInt64(6)),
            reader.GetInt64(7),
            reader.GetInt64(8) != 0);

    private static string GuidText(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
    private static object DbGuid(Guid? value) => value is null ? DBNull.Value : GuidText(value.Value);
    private static object DbValue(string? value) => value is null ? DBNull.Value : value;
    private static long BooleanInteger(bool value) => value ? 1L : 0L;
    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "D");
    private static string TimestampText(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
}
