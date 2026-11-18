using Microsoft.Data.Sqlite;
using SuperBar.Core.Settings;

namespace SuperBar.Infrastructure.Persistence;

/// <summary>
/// Key/value store of JSON documents in SQLite. Each settings area (appearance, layout, widgets, ...)
/// is one row, so areas evolve independently and a corrupt row only resets that area.
/// </summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly string _connectionString;

    public SqliteSettingsStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS settings (
                key         TEXT PRIMARY KEY NOT NULL,
                value       TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public string? Read(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Write(string key, string json)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO settings (key, value, updated_utc) VALUES ($key, $value, $updated)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", json);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Delete(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
