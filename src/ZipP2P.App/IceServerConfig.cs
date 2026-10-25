namespace ZipP2P.App;

/// <summary>
/// Servidor STUN/TURN (ex.: coturn) editado em Configurações. Vira as URLs de ICE do
/// libdatachannel: <c>stun:host:porta</c> e, com usuário, <c>turn:usuario:senha@host:porta</c>.
/// </summary>
public sealed record IceServerConfig(string Host, string Username, string Password)
{
    public const int DefaultPort = 3478;

    public static readonly IceServerConfig Empty = new(string.Empty, string.Empty, string.Empty);

    /// <summary>URLs para <see cref="AppSettings.IceServers"/>; vazio sem host (só conexão direta).</summary>
    public IReadOnlyList<string> ToUrls()
    {
        var host = WithPort(StripScheme(Host.Trim()));
        if (host.Length == 0)
            return Array.Empty<string>();

        var urls = new List<string> { "stun:" + host };
        var username = Username.Trim();
        if (username.Length > 0)
            urls.Add($"turn:{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(Password)}@{host}");
        return urls;
    }

    /// <summary>Lê de volta as URLs salvas: host e credenciais do <c>turn:</c>, senão o host do <c>stun:</c>.</summary>
    public static IceServerConfig FromUrls(IEnumerable<string> urls)
    {
        string? stunHost = null;
        foreach (var url in urls)
        {
            if (url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = url["turn:".Length..];
                var at = rest.LastIndexOf('@');
                if (at < 0)
                    return new IceServerConfig(rest, string.Empty, string.Empty);

                var credentials = rest[..at];
                var colon = credentials.IndexOf(':');
                var username = colon < 0 ? credentials : credentials[..colon];
                var password = colon < 0 ? string.Empty : credentials[(colon + 1)..];
                return new IceServerConfig(rest[(at + 1)..], Uri.UnescapeDataString(username), Uri.UnescapeDataString(password));
            }

            if (url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
                stunHost ??= url["stun:".Length..];
        }

        return stunHost is null ? Empty : new IceServerConfig(stunHost, string.Empty, string.Empty);
    }

    /// <summary>Aceita "turn:host" ou "stun:host" colado no campo do host.</summary>
    private static string StripScheme(string host)
    {
        foreach (var scheme in new[] { "stun:", "turn:" })
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return host[scheme.Length..];
        }
        return host;
    }

    private static string WithPort(string host)
        => host.Length == 0 || host.LastIndexOf(':') > host.LastIndexOf(']') ? host : $"{host}:{DefaultPort}";
}
