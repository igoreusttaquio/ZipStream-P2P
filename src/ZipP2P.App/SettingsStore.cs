using Microsoft.Data.Sqlite;

namespace ZipP2P.App;

/// <summary>Armazenamento de configurações em SQLite (chave/valor).</summary>
public sealed class SettingsStore
{
    private readonly string _connectionString;

    public SettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZipStream");
        Directory.CreateDirectory(directory);

        var dbPath = Path.Combine(directory, "settings.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public string? Get(string key)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO settings (key, value) VALUES ($key, $value) " +
            "ON CONFLICT(key) DO UPDATE SET value = $value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
