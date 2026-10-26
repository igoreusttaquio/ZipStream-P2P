using ZipP2P.P2P.Transfer;

namespace ZipP2P.P2P.Tests;

/// <summary>Envio que começa antes de a compressão terminar (<see cref="PackageSource"/> crescendo).</summary>
public sealed class StreamingSendTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-stream-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Read_WaitsForTheProducer_AndEndsWhenItCompletes()
    {
        var gate = new TaskCompletionSource();
        var package = PackageSource.Start(Path.Combine(_dir, "p.zpp2"), async (output, ct) =>
        {
            output.Write(new byte[] { 1, 2, 3 });
            await gate.Task.WaitAsync(ct);
            output.Write(new byte[] { 4, 5 });
        });

        await using var file = package.OpenRead();
        var buffer = new byte[10];
        Assert.Equal(3, await package.ReadAsync(file, 0, buffer, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(package.Length);

        var waiting = package.ReadAsync(file, 3, buffer, CancellationToken.None).AsTask();
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        gate.SetResult();
        Assert.Equal(2, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new byte[] { 4, 5 }, buffer[..2]);
        Assert.Equal(0, await package.ReadAsync(file, 5, buffer, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(5, package.Length);
    }

    [Fact]
    public async Task ProducerFailure_SurfacesToTheReader_AndDeletesThePartialPackage()
    {
        var path = Path.Combine(_dir, "p.zpp2");
        var package = PackageSource.Start(path, async (output, ct) =>
        {
            output.Write(new byte[] { 1 });
            await Task.Yield();
            throw new IOException("disk full");
        });

        await using (var file = package.OpenRead())
        {
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                for (long position = 0; ; position++)
                {
                    if (await package.ReadAsync(file, position, new byte[1], CancellationToken.None) == 0)
                        break;
                }
            });
        }

        Assert.True(package.Completion.IsFaulted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Cancel_StopsTheCompression()
    {
        var package = PackageSource.Start(Path.Combine(_dir, "p.zpp2"), (output, ct) => Task.Delay(Timeout.Infinite, ct));

        package.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => package.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Session_SendsWhileCompressing_ThenAnnouncesTheFinalSize()
    {
        // Bytes "do pacote" maiores que a janela de fluxo, liberados em duas etapas.
        var data = new byte[(int)TransferSession.WindowSize * 2 + 4321];
        new Random(7).NextBytes(data);
        var gate = new TaskCompletionSource();
        var package = PackageSource.Start(Path.Combine(_dir, "p.zpp2"), async (output, ct) =>
        {
            output.Write(data.AsSpan(0, data.Length / 3));
            await gate.Task.WaitAsync(ct);
            output.Write(data.AsSpan(data.Length / 3));
        });

        var partial = Path.Combine(_dir, "recv.part");
        var (a, b) = MemoryTransport.CreatePair();
        var senderProgress = new List<TransferProgress>();
        var receiverProgress = new List<TransferProgress>();
        TransferHeader? announced = null;

        var received = new TransferSession(b).ReceiveAsync(
            h =>
            {
                announced = h;
                return partial;
            },
            new SyncProgress<TransferProgress>(receiverProgress.Add));
        var sent = new TransferSession(a).SendAsync(
            package, new TransferHeader(Guid.NewGuid(), "big.bin", -1, 0), new SyncProgress<TransferProgress>(senderProgress.Add));

        // Envia o que já existe e espera a compressão continuar (o destinatário grava em disco a cada 1 MB confirmado).
        await WaitUntilAsync(() => File.Exists(partial) && new FileInfo(partial).Length >= data.Length / 3 / (1024 * 1024) * (1024 * 1024));
        await Task.Delay(100);
        Assert.False(sent.IsCompleted);
        gate.SetResult();

        await sent.WaitAsync(TimeSpan.FromSeconds(30));
        var header = await received.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(-1, announced!.Size);
        Assert.True(announced.EstimatedSize > 0);
        Assert.Equal(data.Length, header.Size);
        Assert.Equal(data, File.ReadAllBytes(partial));
        Assert.Contains(receiverProgress, p => p.TotalIsEstimate);
        foreach (var last in new[] { senderProgress[^1], receiverProgress[^1] })
        {
            Assert.False(last.TotalIsEstimate);
            Assert.Equal(data.Length, last.BytesTransferred);
            Assert.Equal(data.Length, last.TotalBytes);
        }
    }

    [Fact]
    public async Task Session_ResumesWhileCompressing()
    {
        var data = new byte[3 * 1024 * 1024];
        new Random(8).NextBytes(data);
        var partial = Path.Combine(_dir, "recv.part");
        File.WriteAllBytes(partial, data.AsSpan(0, data.Length / 2).ToArray());
        var gate = new TaskCompletionSource();
        var package = PackageSource.Start(Path.Combine(_dir, "p.zpp2"), async (output, ct) =>
        {
            output.Write(data.AsSpan(0, data.Length * 3 / 4));
            await gate.Task.WaitAsync(ct);
            output.Write(data.AsSpan(data.Length * 3 / 4));
        });

        var (a, b) = MemoryTransport.CreatePair();
        var received = new TransferSession(b).ReceiveAsync(_ => partial);
        var sent = new TransferSession(a).SendAsync(package, new TransferHeader(Guid.NewGuid(), "f.bin", -1, 0));
        gate.SetResult();
        await sent.WaitAsync(TimeSpan.FromSeconds(30));
        await received.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(data, File.ReadAllBytes(partial));
        // Só a metade restante trafegou.
        Assert.True(a.MessagesSent < data.Length / 2 / TransferSession.ChunkSize + 10, $"sent {a.MessagesSent} messages");
    }

    [Fact]
    public async Task Runner_CompressionFailureWhileWaitingForThePeer_DoesNotHang()
    {
        var store = new TransferStore(Path.Combine(_dir, "store"));
        var transfer = new OutgoingTransfer(Guid.NewGuid(), "src", "f.bin", 0, -1, "ROOM", DateTimeOffset.Now);
        var package = PackageSource.Start(store.PackagePath(transfer.Id), async (output, ct) =>
        {
            await Task.Delay(100, ct);
            throw new IOException("the source file is locked");
        });

        // Ninguém entra na sala: sem o tratamento, o remetente esperaria o par para sempre.
        var runner = new TransferRunner(new MemoryPeerLinkFactory(), store);
        var error = await Assert.ThrowsAsync<IOException>(
            () => runner.SendAsync(transfer, package, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("the source file is locked", error.Message);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }
}
