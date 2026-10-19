using System.Text.Json;

namespace ZipP2P.App;

/// <summary>Configurações do aplicativo, carregadas de um arquivo settings.json.</summary>
public sealed record AppSettings
{
    /// <summary>URL do servidor de sinalização (SignalR).</summary>
    public string SignalingUrl { get; init; } = "http://localhost:5000/signaling";

    /// <summary>Servidores ICE (STUN/TURN), no formato "stun:host:port" ou "turn:user:pass@host:port".</summary>
    public List<string> IceServers { get; init; } = new();

    public static AppSettings Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "settings.json");
        if (!File.Exists(path))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}
