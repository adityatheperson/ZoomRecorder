using Microsoft.Data.Sqlite;
using ZoomRecorder.App.ViewModels.Library;

namespace ZoomRecorder.App.Data;

public sealed class SqliteAppSettingsStore(LibraryDatabase database) : IAppSettingsStore
{
    private const string DeleteVideoKey = "delete_video_after_processing_default";
    private readonly LibraryDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<bool> GetDeleteVideoDefaultAsync(CancellationToken cancellationToken)
    {
        await database.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", DeleteVideoKey);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string text && string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }
        finally { database.Gate.Release(); }
    }

    public async Task SetDeleteVideoDefaultAsync(bool value, CancellationToken cancellationToken)
    {
        await database.Gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_settings(key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", DeleteVideoKey);
            command.Parameters.AddWithValue("$value", value ? "true" : "false");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { database.Gate.Release(); }
    }
}
