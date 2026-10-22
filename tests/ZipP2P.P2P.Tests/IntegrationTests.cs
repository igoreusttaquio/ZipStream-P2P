using Microsoft.AspNetCore.Builder;
using ZipP2P.Core.Compression;
using ZipP2P.P2P.Transfer;
using ZipP2P.Server;

namespace ZipP2P.P2P.Tests;

/// <summary>Ponta a ponta com o servidor de sinalização real (SignalR) e libdatachannel, em loopback.</summary>
public sealed class IntegrationTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-it-").FullName;
    private readonly int _port = Random.Shared.Next(20000, 40000);
    private WebApplication? _server;

    private string Url => $"http://127.0.0.1:{_port}/signaling";

    public async Task InitializeAsync()
    {
        _server = SignalingServer.Build($"http://127.0.0.1:{_port}");
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server!.StopAsync();
        await _server.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task LargeFile_TransfersEndToEnd_OverWebRtc()
    {
        // Antes da correção, o remetente fechava a conexão com dados no buffer e o
        // destinatário ficava esperando; aqui o pacote (incomprimível) tem ~40 MB.
        var source = Path.Combine(_dir, "big.bin");
        var data = new byte[40 * 1024 * 1024];
        new Random(3).NextBytes(data);
        await File.WriteAllBytesAsync(source, data);

        var senderStore = new TransferStore(Path.Combine(_dir, "s"));
        var receiverStore = new TransferStore(Path.Combine(_dir, "r"));
        var id = Guid.NewGuid();
        var type = await TransferPackage.CreateAsync(source, senderStore.PackagePath(id), new CompressionOptions { Level = 1 });
        var outgoing = new OutgoingTransfer(id, source, "big.bin", type, new FileInfo(senderStore.PackagePath(id)).Length, "IT1", DateTimeOffset.Now);
        senderStore.Save(outgoing);

        var factory = new SignalingPeerLinkFactory(Url);
        string? sasA = null, sasB = null;
        var sender = new TransferRunner(factory, senderStore);
        var receiver = new TransferRunner(factory, receiverStore);
        sender.SasReady += s => sasA = s;
        receiver.SasReady += s => sasB = s;

        var receiving = receiver.ReceiveAsync("IT1", Path.Combine(_dir, "dl"), null, CancellationToken.None);
        await Task.Delay(300);
        await sender.SendAsync(outgoing, null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        var incoming = await receiving.WaitAsync(TimeSpan.FromMinutes(2));

        var extracted = await TransferPackage.ExtractAsync(
            receiverStore.PartialPath(incoming.Id), incoming.FileName, incoming.ContentType, incoming.DestinationDir);
        Assert.Equal(data, await File.ReadAllBytesAsync(extracted));
        Assert.Equal(sasA, sasB);
    }

    [Fact]
    public async Task ThirdDevice_CannotJoinAnOccupiedRoom()
    {
        await using var a = new SignalRSignalingClient(Url);
        await using var b = new SignalRSignalingClient(Url);
        await using var c = new SignalRSignalingClient(Url);
        foreach (var client in new[] { a, b, c })
            await client.StartAsync();

        await a.JoinRoomAsync("FULL");
        await b.JoinRoomAsync("FULL");
        await Assert.ThrowsAsync<SignalingException>(() => c.JoinRoomAsync("FULL"));
    }

    [Fact]
    public async Task PeerJoined_IsAlsoSentToTheLateJoiner()
    {
        await using var early = new SignalRSignalingClient(Url);
        await using var late = new SignalRSignalingClient(Url);
        var lateNotified = new TaskCompletionSource();
        late.PeerJoined += () => lateNotified.TrySetResult();
        await early.StartAsync();
        await late.StartAsync();

        await early.JoinRoomAsync("LATE");
        await late.JoinRoomAsync("LATE");

        await lateNotified.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
