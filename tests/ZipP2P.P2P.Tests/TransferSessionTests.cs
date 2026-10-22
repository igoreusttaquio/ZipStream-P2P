using System.Security.Cryptography;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.P2P.Tests;

public sealed class TransferSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-session-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task LargePackage_IsDeliveredCompletely_WithFlowControl()
    {
        // Maior que a janela: exercita a espera por confirmações (causa do bug de arquivos grandes).
        var package = CreateRandomFile("package.zpp2", (int)(TransferSession.WindowSize * 3 + 12345));
        var header = new TransferHeader(Guid.NewGuid(), "big.bin", new FileInfo(package).Length, 0);
        var partial = Path.Combine(_dir, "recv.part");
        var (a, b) = MemoryTransport.CreatePair();
        var sender = new TransferSession(a);
        var receiver = new TransferSession(b);

        string? senderSas = null, receiverSas = null;
        sender.SasReady += s => senderSas = s;
        receiver.SasReady += s => receiverSas = s;

        var received = receiver.ReceiveAsync(_ => partial);
        await sender.SendAsync(package, header).WaitAsync(TimeSpan.FromSeconds(30));
        var receivedHeader = await received.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(header, receivedHeader);
        Assert.Equal(Hash(package), Hash(partial));
        Assert.NotNull(senderSas);
        Assert.Equal(senderSas, receiverSas);
    }

    [Fact]
    public async Task ExistingPartialFile_ResumesFromItsLength()
    {
        var package = CreateRandomFile("package.zpp2", 3 * 1024 * 1024);
        var size = new FileInfo(package).Length;
        var header = new TransferHeader(Guid.NewGuid(), "file.bin", size, 0);
        var partial = Path.Combine(_dir, "recv.part");
        File.WriteAllBytes(partial, File.ReadAllBytes(package).AsSpan(0, (int)(size / 2)).ToArray());

        var (a, b) = MemoryTransport.CreatePair();
        var receiver = new TransferSession(b);
        var received = receiver.ReceiveAsync(_ => partial);
        var progress = new List<TransferProgress>();
        await new TransferSession(a).SendAsync(package, header, new SyncProgress<TransferProgress>(progress.Add)).WaitAsync(TimeSpan.FromSeconds(30));
        await received.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(Hash(package), Hash(partial));
        // Só a metade restante trafegou: ~ (size/2) / ChunkSize chunks + controle.
        Assert.True(a.MessagesSent < size / 2 / TransferSession.ChunkSize + 10, $"sent {a.MessagesSent} messages");
        Assert.Equal(size, progress[^1].BytesTransferred);
    }

    [Fact]
    public async Task PeerAbort_SurfacesReasonOnTheOtherSide()
    {
        var (a, b) = MemoryTransport.CreatePair();
        var receiver = new TransferSession(b);
        var received = receiver.ReceiveAsync(_ => Path.Combine(_dir, "x.part"));

        await new TransferSession(a).AbortAsync("cancelled by sender");

        var ex = await Assert.ThrowsAsync<TransferAbortedException>(() => received.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("cancelled by sender", ex.Reason);
    }

    [Fact]
    public async Task SilentPeer_ThrowsConnectionLost_AfterIdleTimeout()
    {
        var (_, b) = MemoryTransport.CreatePair();
        var receiver = new TransferSession(b, idleTimeout: TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<ConnectionLostException>(
            () => receiver.ReceiveAsync(_ => Path.Combine(_dir, "x.part")).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ConnectionDrop_ThrowsConnectionLost_OnBothSides()
    {
        var package = CreateRandomFile("package.zpp2", 2 * 1024 * 1024);
        var header = new TransferHeader(Guid.NewGuid(), "file.bin", new FileInfo(package).Length, 0);
        var (a, b) = MemoryTransport.CreatePair();
        a.DropAfterMessages = 10;

        var received = new TransferSession(b).ReceiveAsync(_ => Path.Combine(_dir, "x.part"));
        var sent = new TransferSession(a).SendAsync(package, header);

        await Assert.ThrowsAsync<ConnectionLostException>(() => sent.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<ConnectionLostException>(() => received.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private string CreateRandomFile(string name, int size)
    {
        var path = Path.Combine(_dir, name);
        var data = new byte[size];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>IProgress que reporta de forma síncrona (o Progress&lt;T&gt; posta no thread pool).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value)
    {
        lock (this)
            report(value);
    }
}
