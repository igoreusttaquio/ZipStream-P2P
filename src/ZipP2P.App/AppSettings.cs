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
    private const string DownloadDirectoryKey = "DownloadDirectory";

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

    /// <summary>Pasta onde os recebimentos são salvos (padrão: Downloads do usuário).</summary>
    public string DownloadDirectory
    {
        get => _store.Get(DownloadDirectoryKey) is { Length: > 0 } dir ? dir : DefaultDownloadDirectory;
        set => _store.Set(DownloadDirectoryKey, value);
    }

    public static string DefaultDownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

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
