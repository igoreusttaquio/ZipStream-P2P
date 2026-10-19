using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;
using ZipP2P.Server;

// Spike Fase 3: transfer�ncia real de uma pasta via sinaliza��o SignalR + libdatachannel.

const string RoomCode = "ABCD-1234";
const string ServerUrl = "http://127.0.0.1:5002/signaling";

var builder = WebApplication.CreateBuilder();
builder.Services.AddSignalR();
builder.WebHost.UseUrls("http://127.0.0.1:5002");
var app = builder.Build();
app.MapHub<SignalingHub>("/signaling");
await app.StartAsync();
Console.WriteLine("[server] sinaliza��o em " + ServerUrl);

var sampleDir = Path.Combine(Path.GetTempPath(), "zpp2-sample-" + Guid.NewGuid().ToString("N"));
var destDir = Path.Combine(Path.GetTempPath(), "zpp2-dest-" + Guid.NewGuid().ToString("N"));

try
{
    // Pasta de exemplo.
    Directory.CreateDirectory(Path.Combine(sampleDir, "sub"));
    File.WriteAllText(Path.Combine(sampleDir, "a.txt"), "ol� mundo");
    File.WriteAllText(Path.Combine(sampleDir, "sub", "b.txt"), "arquivo aninhado");
    var big = new byte[8 * 1024 * 1024];
    new Random(7).NextBytes(big);
    File.WriteAllBytes(Path.Combine(sampleDir, "big.bin"), big);

    var offererTransport = new LibDataChannelTransport();
    var answererTransport = new LibDataChannelTransport();
    await using var offerer = new TransferSession(offererTransport);
    await using var answerer = new TransferSession(answererTransport);
    string? offererSas = null, answererSas = null;
    offerer.SasReady += s => { offererSas = s; Console.WriteLine($"[offerer] SAS = {s}"); };
    answerer.SasReady += s => { answererSas = s; Console.WriteLine($"[answerer] SAS = {s}"); };
    var offererPeer = new PeerConnection(offererTransport, new SignalRSignalingClient(ServerUrl), RoomCode);
    var answererPeer = new PeerConnection(answererTransport, new SignalRSignalingClient(ServerUrl), RoomCode);

    var offererOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var answererOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    offererTransport.Connected += () => offererOpen.TrySetResult();
    answererTransport.Connected += () => answererOpen.TrySetResult();

    var offererConnect = offererPeer.ConnectAsync(asOfferer: true);
    await Task.Delay(500);
    await answererPeer.ConnectAsync(asOfferer: false);
    await offererConnect;

    await Task.WhenAll(offererOpen.Task, answererOpen.Task).WaitAsync(TimeSpan.FromSeconds(15));
    Console.WriteLine("[spike] conectados. Transferindo pasta...");

    var sw = Stopwatch.StartNew();
    await Task.WhenAll(
        offerer.SendAsync(sampleDir),
        answerer.ReceiveAsync(destDir));
    sw.Stop();

    // Verifica��o.
    Console.WriteLine($"a.txt = {File.ReadAllText(Path.Combine(destDir, "a.txt"))}");
    Console.WriteLine($"sub/b.txt = {File.ReadAllText(Path.Combine(destDir, "sub", "b.txt"))}");
    var receivedBig = File.ReadAllBytes(Path.Combine(destDir, "big.bin"));
    Console.WriteLine($"big.bin = {receivedBig.Length} bytes, igual = {receivedBig.AsSpan().SequenceEqual(big)}");
    Console.WriteLine($"SAS iguais = {offererSas == answererSas} ({offererSas})");
    Console.WriteLine($"[spike] OK em {sw.Elapsed.TotalSeconds:F2}s");

    return 0;
}
finally
{
    await app.StopAsync();
    try { Directory.Delete(sampleDir, recursive: true); } catch { }
    try { Directory.Delete(destDir, recursive: true); } catch { }
}
