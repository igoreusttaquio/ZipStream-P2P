using System.Threading.Channels;
using ZipP2P.Core.Security;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.P2P.Tests;

public sealed class SessionAuthenticationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-auth-").FullName;
    private readonly DeviceIdentity _sender = DeviceIdentity.Create();
    private readonly DeviceIdentity _receiver = DeviceIdentity.Create();

    public void Dispose()
    {
        _sender.Dispose();
        _receiver.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task PairedDevices_TransferAndLearnEachOthersIdentity()
    {
        var (package, header) = CreatePackage();
        var partial = Path.Combine(_dir, "recv.part");
        var (a, b) = MemoryTransport.CreatePair();
        var sender = new TransferSession(a, authentication: Trusting(_sender, _receiver));
        var receiver = new TransferSession(b, authentication: Trusting(_receiver, _sender));

        var received = receiver.ReceiveAsync(_ => partial);
        await sender.SendAsync(package, header).WaitAsync(TimeSpan.FromSeconds(30));
        await received.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TransferSessionTests.Hash(package), TransferSessionTests.Hash(partial));
        Assert.Equal(_receiver.PublicKey, sender.PeerIdentityKey);
        Assert.Equal(_sender.PublicKey, receiver.PeerIdentityKey);
    }

    [Fact]
    public async Task UnpairedSender_IsRejectedBeforeAnyDataIsWritten()
    {
        var (package, header) = CreatePackage();
        using var stranger = DeviceIdentity.Create();
        var (a, b) = MemoryTransport.CreatePair();
        var resolved = false;

        var received = new TransferSession(b, authentication: Trusting(_receiver, _sender))
            .ReceiveAsync(_ =>
            {
                resolved = true;
                return Path.Combine(_dir, "recv.part");
            });
        var sending = new TransferSession(a, idleTimeout: TimeSpan.FromSeconds(1), authentication: Trusting(stranger, _receiver))
            .SendAsync(package, header);

        var ex = await Assert.ThrowsAsync<PeerNotTrustedException>(() => received.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("not paired", ex.Message);
        Assert.False(resolved);
        await Assert.ThrowsAsync<ConnectionLostException>(() => sending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ManInTheMiddle_RelayingTheIdentities_IsDetectedOnBothSides()
    {
        // Quem controla a sinalização pode trocar as chaves efêmeras e repassar as mensagens de
        // identidade. A assinatura cobre as chaves efêmeras que cada lado viu, então não confere.
        var (package, header) = CreatePackage();
        var (s, mitmToSender) = MemoryTransport.CreatePair();
        var (mitmToReceiver, r) = MemoryTransport.CreatePair();
        var fromSender = Listen(mitmToSender);
        var fromReceiver = Listen(mitmToReceiver);

        var received = new TransferSession(r, authentication: Trusting(_receiver, _sender))
            .ReceiveAsync(_ => Path.Combine(_dir, "recv.part"));
        var sending = new TransferSession(s, authentication: Trusting(_sender, _receiver))
            .SendAsync(package, header);

        using var withSender = new KeyAgreement();
        using var withReceiver = new KeyAgreement();
        await mitmToSender.SendAsync(Message(0x01, withSender.PublicKey));
        await mitmToReceiver.SendAsync(Message(0x01, withReceiver.PublicKey));
        var senderCipher = new ChunkCipher(withSender.DeriveKey((await fromSender.ReadAsync())[1..].Span));
        var receiverCipher = new ChunkCipher(withReceiver.DeriveKey((await fromReceiver.ReadAsync())[1..].Span));

        var senderIdentity = senderCipher.Decrypt((await fromSender.ReadAsync())[1..].Span);
        var receiverIdentity = receiverCipher.Decrypt((await fromReceiver.ReadAsync())[1..].Span);
        await mitmToReceiver.SendAsync(Message(0x09, receiverCipher.Encrypt(senderIdentity)));
        await mitmToSender.SendAsync(Message(0x09, senderCipher.Encrypt(receiverIdentity)));

        var onReceiver = await Assert.ThrowsAsync<PeerNotTrustedException>(() => received.WaitAsync(TimeSpan.FromSeconds(10)));
        var onSender = await Assert.ThrowsAsync<PeerNotTrustedException>(() => sending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("failed to prove", onReceiver.Message);
        Assert.Contains("failed to prove", onSender.Message);
    }

    [Fact]
    public void Transcript_DependsOnWhoSigns()
    {
        byte[] mine = [1, 2, 3], theirs = [4, 5, 6];

        Assert.NotEqual(SessionAuthentication.Transcript(mine, theirs), SessionAuthentication.Transcript(theirs, mine));
    }

    private static SessionAuthentication Trusting(DeviceIdentity self, DeviceIdentity trusted)
        => new(self, key => key.AsSpan().SequenceEqual(trusted.PublicKey));

    private (string Package, TransferHeader Header) CreatePackage()
    {
        var path = Path.Combine(_dir, "package.zpp2");
        var data = new byte[512 * 1024];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(path, data);
        return (path, new TransferHeader(Guid.NewGuid(), "file.bin", data.Length, 0));
    }

    private static ChannelReader<ReadOnlyMemory<byte>> Listen(MemoryTransport transport)
    {
        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        transport.MessageReceived += m => channel.Writer.TryWrite(m);
        return channel.Reader;
    }

    private static byte[] Message(byte type, byte[] payload) => [type, .. payload];
}
