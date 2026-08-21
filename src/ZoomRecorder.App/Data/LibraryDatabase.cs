using Microsoft.Data.Sqlite;

namespace ZoomRecorder.App.Data;

public sealed class LibraryDatabase : IAsyncDisposable
{
    private const int CurrentSchemaVersion = 2;
    internal const string WindowsPathCollation = "WINDOWS_PATH";

    private LibraryDatabase(SqliteConnection connection)
    {
        Connection = connection;
    }

    internal SqliteConnection Connection { get; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public static async Task<LibraryDatabase> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();

        var canonicalPath = Path.GetFullPath(databasePath);
        var parent = Path.GetDirectoryName(canonicalPath)
            ?? throw new ArgumentException("The database path must have a parent directory.", nameof(databasePath));
        Directory.CreateDirectory(parent);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = canonicalPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.CreateCollation(
            WindowsPathCollation,
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

        try
        {
            await connection.OpenAsync(cancellationToken);
            await EnableForeignKeysAsync(connection, cancellationToken);
            await ApplySchemaAsync(connection, cancellationToken);
            return new LibraryDatabase(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Gate.Dispose();
        await Connection.DisposeAsync();
    }

    private static async Task EnableForeignKeysAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplySchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var tableCheck = connection.CreateCommand();
        tableCheck.Transaction = transaction;
        tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        tableCheck.Parameters.AddWithValue("$name", "schema_info");
        var hasSchemaInfo = Convert.ToInt64(
            await tableCheck.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1;

        if (hasSchemaInfo)
        {
            var version = await ReadSchemaVersionAsync(connection, transaction, cancellationToken);
            if (version == 1)
            {
                await MigrateVersionOneToTwoAsync(connection, transaction, cancellationToken);
            }
            else if (version != CurrentSchemaVersion)
            {
                throw new NotSupportedException($"Library schema version {version} is not supported.");
            }
        }
        else
        {
            await CreateSchemaVersionTwoAsync(connection, transaction, cancellationToken);
        }

        await EnsureWindowsPathIndexAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureWindowsPathIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            CREATE UNIQUE INDEX IF NOT EXISTS recordings_file_path_windows_unique
            ON recordings(file_path COLLATE {WindowsPathCollation});
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_info LIMIT 2;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("The library schema version is missing.");
        }

        var version = reader.GetInt32(0);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("The library contains multiple schema versions.");
        }

        return version;
    }

    private static async Task CreateSchemaVersionTwoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE schema_info(version INTEGER NOT NULL);
            CREATE TABLE classes(id TEXT PRIMARY KEY, name TEXT NOT NULL, term TEXT, created_at TEXT NOT NULL, is_archived INTEGER NOT NULL);
            CREATE TABLE recordings(id TEXT PRIMARY KEY, class_id TEXT NULL REFERENCES classes(id), file_path TEXT NOT NULL UNIQUE, file_name TEXT NOT NULL, meeting_id TEXT, recorded_at TEXT NOT NULL, duration_ms INTEGER NOT NULL, byte_size INTEGER NOT NULL, video_available INTEGER NOT NULL);
            CREATE TABLE meeting_class_mappings(meeting_id TEXT PRIMARY KEY, class_id TEXT NOT NULL REFERENCES classes(id));
            CREATE TABLE processing_jobs(
                id TEXT PRIMARY KEY,
                recording_id TEXT NOT NULL REFERENCES recordings(id),
                class_id TEXT NOT NULL REFERENCES classes(id),
                mp4_path TEXT NOT NULL,
                job_directory TEXT NOT NULL,
                state TEXT NOT NULL,
                failed_stage TEXT,
                delete_video INTEGER NOT NULL,
                error_code TEXT,
                transcript_committed INTEGER NOT NULL,
                lecture_package_committed INTEGER NOT NULL,
                assignments_committed INTEGER NOT NULL,
                guide_outcome TEXT NOT NULL,
                revision INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE UNIQUE INDEX processing_jobs_directory_windows_unique
                ON processing_jobs(job_directory COLLATE WINDOWS_PATH);
            CREATE TRIGGER processing_job_class_matches_recording
                BEFORE INSERT ON processing_jobs
                WHEN (SELECT class_id FROM recordings WHERE id = NEW.recording_id) IS NOT NEW.class_id
                BEGIN
                    SELECT RAISE(ABORT, 'processing job class does not match recording');
                END;
            CREATE TABLE audio_chunks(
                job_id TEXT NOT NULL REFERENCES processing_jobs(id) ON DELETE CASCADE,
                chunk_index INTEGER NOT NULL,
                start_ms INTEGER NOT NULL,
                end_ms INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                artifact_path TEXT NOT NULL,
                byte_size INTEGER NOT NULL,
                PRIMARY KEY(job_id, chunk_index));
            CREATE TABLE transcription_chunks(
                job_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                audio_sha256 TEXT NOT NULL,
                artifact_path TEXT NOT NULL,
                artifact_sha256 TEXT NOT NULL,
                PRIMARY KEY(job_id, chunk_index),
                FOREIGN KEY(job_id, chunk_index) REFERENCES audio_chunks(job_id, chunk_index) ON DELETE CASCADE);
            CREATE TABLE processing_transcripts(
                job_id TEXT PRIMARY KEY REFERENCES processing_jobs(id) ON DELETE CASCADE,
                artifact_path TEXT NOT NULL,
                artifact_sha256 TEXT NOT NULL);
            CREATE TABLE lecture_packages(recording_id TEXT PRIMARY KEY REFERENCES recordings(id), schema_version INTEGER NOT NULL, artifact_path TEXT NOT NULL, artifact_sha256 TEXT NOT NULL, source_transcript_hash TEXT NOT NULL, is_stale INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE assignments(id TEXT PRIMARY KEY, recording_id TEXT NOT NULL REFERENCES recordings(id), description TEXT NOT NULL, due_date_text TEXT, due_at TEXT, confidence REAL NOT NULL, is_user_confirmed INTEGER NOT NULL, source_timestamp_ms INTEGER, source_order INTEGER NOT NULL);
            CREATE TABLE class_study_guides(class_id TEXT PRIMARY KEY REFERENCES classes(id), schema_version INTEGER NOT NULL, artifact_path TEXT NOT NULL, artifact_sha256 TEXT NOT NULL, is_update_pending INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE app_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO schema_info(version) VALUES ($version);
            """;
        command.Parameters.AddWithValue("$version", CurrentSchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateVersionOneToTwoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE processing_jobs ADD COLUMN class_id TEXT REFERENCES classes(id);
            ALTER TABLE processing_jobs ADD COLUMN mp4_path TEXT;
            ALTER TABLE processing_jobs ADD COLUMN job_directory TEXT;
            ALTER TABLE processing_jobs ADD COLUMN failed_stage TEXT;
            ALTER TABLE processing_jobs ADD COLUMN transcript_committed INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE processing_jobs ADD COLUMN lecture_package_committed INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE processing_jobs ADD COLUMN assignments_committed INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE processing_jobs ADD COLUMN guide_outcome TEXT NOT NULL DEFAULT 'NotAttempted';
            ALTER TABLE processing_jobs ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE processing_jobs ADD COLUMN created_at TEXT;
            CREATE UNIQUE INDEX processing_jobs_directory_windows_unique
                ON processing_jobs(job_directory COLLATE WINDOWS_PATH);
            CREATE TRIGGER processing_job_class_matches_recording
                BEFORE INSERT ON processing_jobs
                WHEN NEW.class_id IS NOT NULL AND
                     (SELECT class_id FROM recordings WHERE id = NEW.recording_id) IS NOT NEW.class_id
                BEGIN
                    SELECT RAISE(ABORT, 'processing job class does not match recording');
                END;

            ALTER TABLE transcription_chunks RENAME TO audio_chunks;
            ALTER TABLE audio_chunks ADD COLUMN byte_size INTEGER;
            CREATE TABLE transcription_chunks(
                job_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                audio_sha256 TEXT NOT NULL,
                artifact_path TEXT NOT NULL,
                artifact_sha256 TEXT NOT NULL,
                PRIMARY KEY(job_id, chunk_index),
                FOREIGN KEY(job_id, chunk_index) REFERENCES audio_chunks(job_id, chunk_index) ON DELETE CASCADE);
            CREATE TABLE processing_transcripts(
                job_id TEXT PRIMARY KEY REFERENCES processing_jobs(id) ON DELETE CASCADE,
                artifact_path TEXT NOT NULL,
                artifact_sha256 TEXT NOT NULL);

            ALTER TABLE lecture_packages ADD COLUMN artifact_sha256 TEXT NOT NULL
                DEFAULT '0000000000000000000000000000000000000000000000000000000000000000';
            ALTER TABLE assignments ADD COLUMN source_order INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE class_study_guides ADD COLUMN artifact_sha256 TEXT NOT NULL
                DEFAULT '0000000000000000000000000000000000000000000000000000000000000000';
            UPDATE schema_info SET version = 2;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
