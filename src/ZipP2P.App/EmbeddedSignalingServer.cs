using Microsoft.AspNetCore.Builder;
using ZipP2P.Server;

namespace ZipP2P.App;

/// <summary>Servidor de sinalização embutido no app (para transferências na mesma rede).</summary>
public sealed class EmbeddedSignalingServer : IAsyncDisposable
{
    public static EmbeddedSignalingServer Instance { get; } = new();

    private WebApplication? _app;

    /// <summary>Disparado quando o servidor é iniciado ou parado.</summary>
    public event Action? StateChanged;

    public bool IsRunning => _app is not null;

    public async Task StartAsync(string url = "http://0.0.0.0:5000")
    {
        if (_app is not null)
            return;

        _app = SignalingServer.Build(url);
        await _app.StartAsync();
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
