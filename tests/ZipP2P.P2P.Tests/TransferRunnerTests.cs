using ZipP2P.Core.Compression;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.P2P.Tests;

public sealed class TransferRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-runner-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ConnectionDrops_AreRecovered_AndTransferResumes()
    {
        var source = Path.Combine(_dir, "video.bin");
        var data = new byte[6 * 1024 * 1024];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(source, data);

        var senderStore = new TransferStore(Path.Combine(_dir, "sender"));
        var receiverStore = new TransferStore(Path.Combine(_dir, "receiver"));
        var outgoing = await PrepareAsync(senderStore, source, "ROOM42");

        // As duas primeiras conexões caem no meio; a terceira completa.
        var factory = new MemoryPeerLinkFactory
        {
            OnPair = (index, offerer, _) =>
            {
                if (index < 2)
                    offerer.DropAfterMessages = 40;
            },
        };

        var sender = new TransferRunner(factory, senderStore, backoff: _ => TimeSpan.FromMilliseconds(50));
        var receiver = new TransferRunner(factory, receiverStore, backoff: _ => TimeSpan.FromMilliseconds(50));
        var reconnects = 0;
        sender.StatusChanged += s =>
        {
            if (s.Stage == TransferStage.Reconnecting)
                Interlocked.Increment(ref reconnects);
        };

        var destination = Path.Combine(_dir, "Downloads");
        var receiving = receiver.ReceiveAsync("ROOM42", destination, null, CancellationToken.None);
        await sender.SendAsync(outgoing, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        var incoming = await receiving.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(3, factory.Pairs);
        Assert.True(reconnects >= 2);
        Assert.Empty(senderStore.ListOutgoing());

        var extracted = await TransferPackage.ExtractAsync(
            receiverStore.PartialPath(incoming.Id), incoming.FileName, incoming.ContentType, incoming.DestinationDir);
        Assert.Equal(Path.Combine(destination, "video.bin"), extracted);
        Assert.Equal(data, File.ReadAllBytes(extracted));
    }

    [Fact]
    public async Task PendingDownload_IsResumed_ByANewRunner()
    {
        // Simula "fechar e reabrir o app": a primeira tentativa cai e o usuário cancela;
        // depois, novos runners (novo processo) retomam a partir do que já estava em disco.
        var source = Path.Combine(_dir, "doc.bin");
        var data = new byte[4 * 1024 * 1024];
        new Random(2).NextBytes(data);
        File.WriteAllBytes(source, data);

        var senderStore = new TransferStore(Path.Combine(_dir, "sender"));
        var receiverStore = new TransferStore(Path.Combine(_dir, "receiver"));
        var outgoing = await PrepareAsync(senderStore, source, "ROOM7");

        using (var cts = new CancellationTokenSource())
        {
            var factory = new MemoryPeerLinkFactory { OnPair = (_, offerer, _) => offerer.DropAfterMessages = 30 };
            var sender = new TransferRunner(factory, senderStore, backoff: _ => TimeSpan.FromSeconds(30));
            var receiver = new TransferRunner(factory, receiverStore, backoff: _ => TimeSpan.FromSeconds(30));
            sender.StatusChanged += s =>
            {
                if (s.Stage == TransferStage.Reconnecting)
                    cts.Cancel();
            };

            var r = receiver.ReceiveAsync("ROOM7", Path.Combine(_dir, "out"), null, cts.Token);
            var s = sender.SendAsync(outgoing, null, cts.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.WaitAsync(TimeSpan.FromSeconds(30)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.WaitAsync(TimeSpan.FromSeconds(30)));
        }

        var pendingIn = Assert.Single(receiverStore.ListIncoming());
        var alreadyReceived = receiverStore.BytesReceived(pendingIn);
        Assert.InRange(alreadyReceived, 1, outgoing.PackageSize - 1);
        var pendingOut = Assert.Single(senderStore.ListOutgoing());

        var factory2 = new MemoryPeerLinkFactory();
        var progress = new List<TransferProgress>();
        var receiving = new TransferRunner(factory2, receiverStore)
            .ReceiveAsync(pendingIn.RoomCode, "ignored-uses-original", null, CancellationToken.None);
        await new TransferRunner(factory2, senderStore)
            .SendAsync(pendingOut, new SyncProgress<TransferProgress>(progress.Add), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
        var incoming = await receiving.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(pendingIn.DestinationDir, incoming.DestinationDir);
        Assert.True(progress[0].BytesTransferred > alreadyReceived - 1, "should not restart from zero");
        var extracted = await TransferPackage.ExtractAsync(
            receiverStore.PartialPath(incoming.Id), incoming.FileName, incoming.ContentType, incoming.DestinationDir);
        Assert.Equal(data, File.ReadAllBytes(extracted));
    }

    [Fact]
    public async Task Cancel_NotifiesThePeer()
    {
        var source = Path.Combine(_dir, "a.bin");
        // Maior que a janela: o remetente ainda está enviando quando o cancelamento chega.
        var data = new byte[20 * 1024 * 1024];
        new Random(4).NextBytes(data);
        File.WriteAllBytes(source, data);
        var senderStore = new TransferStore(Path.Combine(_dir, "sender"));
        var outgoing = await PrepareAsync(senderStore, source, "R");

        var factory = new MemoryPeerLinkFactory();
        using var cts = new CancellationTokenSource();
        var receiver = new TransferRunner(factory, new TransferStore(Path.Combine(_dir, "receiver")));
        var receiving = receiver.ReceiveAsync("R", _dir, new SyncProgress<TransferProgress>(_ => cts.Cancel()), CancellationToken.None);
        var sending = new TransferRunner(factory, senderStore).SendAsync(outgoing, null, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<TransferAbortedException>(() => receiving.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private static async Task<OutgoingTransfer> PrepareAsync(TransferStore store, string source, string room)
    {
        var id = Guid.NewGuid();
        var packagePath = store.PackagePath(id);
        var contentType = await TransferPackage.CreateAsync(source, packagePath, new CompressionOptions { Level = 1 });
        var transfer = new OutgoingTransfer(
            id, source, Path.GetFileName(source), contentType, new FileInfo(packagePath).Length, room, DateTimeOffset.Now);
        store.Save(transfer);
        return transfer;
    }
}
