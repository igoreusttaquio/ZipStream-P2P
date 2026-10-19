using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ZipP2P.Server;

/// <summary>Constrói o servidor de sinalização (pode ser executado standalone ou embutido no app).</summary>
public static class SignalingServer
{
    public static WebApplication Build(string url = "http://0.0.0.0:5000")
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSignalR();
        builder.WebHost.UseUrls(url);

        var app = builder.Build();
        app.MapGet("/", () => "ZipStream signaling server");
        app.MapHub<SignalingHub>("/signaling");
        return app;
    }
}
