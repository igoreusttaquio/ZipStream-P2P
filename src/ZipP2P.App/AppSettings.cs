using System.Globalization;
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
    private const string CloseToTrayKey = "CloseToTray";
    private const string AutoCheckUpdatesKey = "AutoCheckUpdates";
    private const string LastUpdateCheckKey = "LastUpdateCheck";
    private const string ReceiveModeEnabledKey = "ReceiveModeEnabled";
    private const string NotificationsEnabledKey = "NotificationsEnabled";

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

    /// <summary>Fechar a janela principal mantém o app na bandeja (padrão: sim).</summary>
    public bool CloseToTray
    {
        get => _store.Get(CloseToTrayKey) != "false";
        set => _store.Set(CloseToTrayKey, value ? "true" : "false");
    }

    /// <summary>Receive mode ligado: volta a ligar sozinho quando o app abre (padrão: não).</summary>
    public bool ReceiveModeEnabled
    {
        get => _store.Get(ReceiveModeEnabledKey) == "true";
        set => _store.Set(ReceiveModeEnabledKey, value ? "true" : "false");
    }

    /// <summary>Notificações do sistema para o modo de recebimento (recebido, pareado, falha). Padrão: sim.</summary>
    public bool NotificationsEnabled
    {
        get => _store.Get(NotificationsEnabledKey) != "false";
        set => _store.Set(NotificationsEnabledKey, value ? "true" : "false");
    }

    /// <summary>Procurar versões novas ao abrir o app, no máximo uma vez por dia (padrão: sim).</summary>
    public bool AutoCheckUpdates
    {
        get => _store.Get(AutoCheckUpdatesKey) != "false";
        set => _store.Set(AutoCheckUpdatesKey, value ? "true" : "false");
    }

    public DateTimeOffset? LastUpdateCheck
    {
        get => DateTimeOffset.TryParse(_store.Get(LastUpdateCheckKey), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
        set => _store.Set(LastUpdateCheckKey, value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
    }

    public static string DefaultDownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// STUN público usado quando nada foi configurado: sem STUN, cada lado só conhece os
    /// próprios IPs da rede local e a conexão entre redes diferentes falha.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultIceServers = new[] { "stun:stun.l.google.com:19302" };

    public IReadOnlyList<string> IceServers
    {
        get
        {
            var json = _store.Get(IceServersKey);
            var servers = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<string>>(json);
            return servers is { Count: > 0 } ? servers : DefaultIceServers;
        }
        set => _store.Set(IceServersKey, JsonSerializer.Serialize(value));
    }
}
