using System.Text.Json;

namespace ZipP2P.App;

/// <summary>Acesso tipado às configurações persistidas em SQLite.</summary>
public sealed class AppSettings
{
    public const string DefaultSignalingUrl = "http://localhost:5000/signaling";
    public const int DefaultPort = 5000;

    private const string SignalingUrlKey = "SignalingUrl";
    private const string IceServersKey = "IceServers";
    private const string PortKey = "Port";

    private readonly SettingsStore _store;

    public AppSettings(SettingsStore store)
    {
        _store = store;
    }

    public string SignalingUrl
    {
        get => _store.Get(SignalingUrlKey) ?? DefaultSignalingUrl;
        set => _store.Set(SignalingUrlKey, value);
    }

    public int Port
    {
        get => int.TryParse(_store.Get(PortKey), out var p) ? p : DefaultPort;
        set => _store.Set(PortKey, value.ToString());
    }

    public IReadOnlyList<string> IceServers
    {
        get
        {
            var json = _store.Get(IceServersKey);
            if (string.IsNullOrEmpty(json))
                return Array.Empty<string>();

            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
    }
}
