using System.Diagnostics;
using DataChannelDotnet.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ZipP2P.P2P;
using ZipP2P.Server;

// Spike Fase 2/3: sinalização SignalR (servidor em processo) + dois clientes (offer/answer)
// trocando chunks via libdatachannel. Valida o fluxo de sinalização ponta a ponta.

const int MessageSize = 64 * 1024;
const long TotalBytes = 128L * 1024 * 1024;
const string RoomCode = "ABCD-1234";
const string ServerUrl = "http://127.0.0.1:5002/signaling";

// 1. Sobe o servidor de sinalização em processo.
var builder = WebApplication.CreateBuilder();
builder.Services.AddSignalR();
builder.WebHost.UseUrls("http://127.0.0.1:5002");
var app = builder.Build();
app.MapHub<SignalingHub>("/signaling");
await app.StartAsync();
Console.WriteLine("[server] sinalização em " + ServerUrl);

try
{
    await using var offerer = CreatePeer();
    await using var answerer = CreatePeer();

    var offererOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var answererOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var received = 0L;
    var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var sw = new Stopwatch();

    offerer.Transport.Connected += () => { Console.WriteLine("[offerer] connected"); offererOpen.TrySetResult(); };
    answerer.Transport.Connected += () => { Console.WriteLine("[answerer] connected"); answererOpen.TrySetResult(); };

    answerer.Transport.MessageReceived += data =>
    {
        if (received == 0)
            sw.Start();
        received += data.Length;
        if (received >= TotalBytes)
        {
            sw.Stop();
            allReceived.TrySetResult();
        }
    };

    // Offerer entra primeiro (aguarda o par); depois o answerer entra.
    var offererTask = offerer.ConnectAsync(asOfferer: true);
    await Task.Delay(500);
    await answerer.ConnectAsync(asOfferer: false);
    await offererTask;

    await Task.WhenAll(offererOpen.Task, answererOpen.Task).WaitAsync(TimeSpan.FromSeconds(15));

    Console.WriteLine($"[spike] enviando {TotalBytes / (1024 * 1024)} MB em mensagens de {MessageSize / 1024} KB...");

    var buffer = new byte[MessageSize];
    new Random(42).NextBytes(buffer);

    sw.Reset();
    long sent = 0;
    while (sent < TotalBytes)
    {
        int length = (int)Math.Min(MessageSize, TotalBytes - sent);
        await offerer.Transport.SendAsync(buffer.AsMemory(0, length));
        sent += length;
    }

    await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(60));

    double seconds = sw.Elapsed.TotalSeconds;
    double mbps = received / (1024.0 * 1024.0) / seconds;
    Console.WriteLine($"[spike] OK: {received / (1024 * 1024)} MB em {seconds:F2}s → {mbps:F1} MB/s");

    return 0;
}
finally
{
    await app.StopAsync();
}

PeerConnection CreatePeer()
{
    var transport = new LibDataChannelTransport(new RtcPeerConfiguration());
    var signaling = new SignalRSignalingClient(ServerUrl);
    return new PeerConnection(transport, signaling, RoomCode);
}
