using System.Globalization;
using Microsoft.Data.Sqlite;
using ZipP2P.Core.Security;

namespace ZipP2P.App.Devices;

/// <summary>Dispositivo pareado: reconhecido pela chave pública da identidade dele.</summary>
public sealed record TrustedDevice(string DeviceId, byte[] PublicKey, string Name, DateTimeOffset PairedAt);

/// <summary>Lista de dispositivos confiáveis (pareados), no mesmo banco SQLite das configurações.</summary>
public sealed class TrustedDeviceStore
{
    private readonly string _connectionString;

    public TrustedDeviceStore()
        : this(SettingsStore.DefaultPath)
    {
    }

    public TrustedDeviceStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS trusted_devices (" +
            "device_id TEXT PRIMARY KEY, public_key BLOB NOT NULL, name TEXT NOT NULL, paired_at TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TrustedDevice> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, public_key, name, paired_at FROM trusted_devices ORDER BY name;";

        var devices = new List<TrustedDevice>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            devices.Add(Read(reader));
        return devices;
    }

    public TrustedDevice? Find(string deviceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, public_key, name, paired_at FROM trusted_devices WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Confia na identidade <paramref name="publicKey"/>. Parear de novo só atualiza o nome.</summary>
    public TrustedDevice Trust(byte[] publicKey, string name)
    {
        var device = new TrustedDevice(DeviceIdentity.ComputeDeviceId(publicKey), publicKey, name.Trim(), DateTimeOffset.Now);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO trusted_devices (device_id, public_key, name, paired_at) VALUES ($id, $key, $name, $at) " +
            "ON CONFLICT(device_id) DO UPDATE SET name = $name;";
        command.Parameters.AddWithValue("$id", device.DeviceId);
        command.Parameters.AddWithValue("$key", publicKey);
        command.Parameters.AddWithValue("$name", device.Name);
        command.Parameters.AddWithValue("$at", device.PairedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return Find(device.DeviceId)!;
    }

    public void Rename(string deviceId, string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trusted_devices SET name = $name WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$name", name.Trim());
        command.ExecuteNonQuery();
    }

    public void Remove(string deviceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM trusted_devices WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static TrustedDevice Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        (byte[])reader[1],
        reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture));
}
