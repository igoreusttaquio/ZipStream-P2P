using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ZipP2P.Core.Compression;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;
using ZipP2P.Server;

// Spike Fase 3: transferência com E2EE + resume, via SignalR + libdatachannel.

const string RoomCode = "ABCD-1234";
const string ServerUrl = "http://127.0.0.1:5002/signaling";

var builder = WebApplication.CreateBuilder();
builder.Services.AddSignalR();
builder.WebHost.UseUrls("http://127.0.0.1:5002");
var app = builder.Build();
app.MapHub<SignalingHub>("/signaling");
await app.StartAsync();

var sampleDir = Path.Combine(Path.GetTempPath(), "zpp2-sample-" + Guid.NewGuid().ToString("N"));
var destDir = Path.Combine(Path.GetTempPath(), "zpp2-dest-" + Guid.NewGuid().ToString("N"));
var fullPath = Path.Combine(Path.GetTempPath(), "zpp2-full-" + Guid.NewGuid().ToString("N") + ".zpp2");
var partialPath = Path.Combine(Path.GetTempPath(), "zpp2-partial-" + Guid.NewGuid().ToString("N") + ".zpp2");

try
{
    Directory.CreateDirectory(Path.Combine(sampleDir, "sub"));
    File.WriteAllText(Path.Combine(sampleDir, "a.txt"), "olá mundo");
    File.WriteAllText(Path.Combine(sampleDir, "sub", "b.txt"), "arquivo aninhado");
    var big = new byte[8 * 1024 * 1024];
    new Random(7).NextBytes(big);
    File.WriteAllBytes(Path.Combine(sampleDir, "big.bin"), big);

    // Pré-comprime (determinístico) e simula uma transferência interrompida na metade.
    await using (var full = File.Create(fullPath))
        await FolderCodec.CompressAsync(sampleDir, full);

    var fullSize = new FileInfo(fullPath).Length;
    var half = fullSize / 2;
    await using (var fullStream = File.OpenRead(fullPath))
    await using (var partial = File.Create(partialPath))
    {
        var buf = new byte[81920];
        long copied = 0;
        while (copied < half)
        {
            var n = await fullStream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, half - copied)));
            await partial.WriteAsync(buf.AsMemory(0, n));
            copied += n;
        }
    }
    Console.WriteLine($"[spike] parcial = {half} de {fullSize} bytes");

    var offererTransport = new LibDataChannelTransport();
    var answererTransport = new LibDataChannelTransport();
    await using var offerer = new TransferSession(offererTransport);
    await using var answerer = new TransferSession(answererTransport);
    string? offererSas = null, answererSas = null;
    offerer.SasReady += s => offererSas = s;
    answerer.SasReady += s => answererSas = s;
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

    // Recebe retomando do parcial.
    var sw = Stopwatch.StartNew();
    await Task.WhenAll(
        offerer.SendAsync(sampleDir),
        answerer.ReceiveAsync(destDir, partialPath));
    sw.Stop();

    Console.WriteLine($"a.txt = {File.ReadAllText(Path.Combine(destDir, "a.txt"))}");
    var receivedBig = File.ReadAllBytes(Path.Combine(destDir, "big.bin"));
    Console.WriteLine($"big.bin = {receivedBig.Length} bytes, igual = {receivedBig.AsSpan().SequenceEqual(big)}");
    Console.WriteLine($"SAS iguais = {offererSas == answererSas} ({offererSas})");
    Console.WriteLine($"[spike] resume OK em {sw.Elapsed.TotalSeconds:F2}s");

    return 0;
}
finally
{
    await app.StopAsync();
    foreach (var p in new[] { sampleDir, destDir, fullPath, partialPath })
    {
        try { if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p); } catch { }
    }
}
