using Microsoft.Data.Sqlite;

namespace ZoomRecorder.App.Data;

public sealed class LibraryDatabase : IAsyncDisposable
{
    private const int CurrentSchemaVersion = 1;
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
            await VerifySchemaVersionAsync(connection, transaction, cancellationToken);
        }
        else
        {
            await CreateSchemaVersionOneAsync(connection, transaction, cancellationToken);
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

    private static async Task VerifySchemaVersionAsync(
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

        if (version != CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Library schema version {version} is not supported.");
        }
    }

    private static async Task CreateSchemaVersionOneAsync(
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
            CREATE TABLE processing_jobs(id TEXT PRIMARY KEY, recording_id TEXT NOT NULL REFERENCES recordings(id), state TEXT NOT NULL, delete_video INTEGER NOT NULL, completed_chunks INTEGER NOT NULL, error_code TEXT, updated_at TEXT NOT NULL);
            CREATE TABLE transcription_chunks(job_id TEXT NOT NULL REFERENCES processing_jobs(id), chunk_index INTEGER NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, sha256 TEXT NOT NULL, artifact_path TEXT, PRIMARY KEY(job_id, chunk_index));
            CREATE TABLE lecture_packages(recording_id TEXT PRIMARY KEY REFERENCES recordings(id), schema_version INTEGER NOT NULL, artifact_path TEXT NOT NULL, source_transcript_hash TEXT NOT NULL, is_stale INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE assignments(id TEXT PRIMARY KEY, recording_id TEXT NOT NULL REFERENCES recordings(id), description TEXT NOT NULL, due_date_text TEXT, due_at TEXT, confidence REAL NOT NULL, is_user_confirmed INTEGER NOT NULL, source_timestamp_ms INTEGER);
            CREATE TABLE class_study_guides(class_id TEXT PRIMARY KEY REFERENCES classes(id), schema_version INTEGER NOT NULL, artifact_path TEXT NOT NULL, is_update_pending INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE app_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO schema_info(version) VALUES ($version);
            """;
        command.Parameters.AddWithValue("$version", CurrentSchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
