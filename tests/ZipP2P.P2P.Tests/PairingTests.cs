using System.Threading.Channels;
using ZipP2P.Core.Security;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.P2P.Tests;

public sealed class PairingTests : IDisposable
{
    private const string Code = "K7P-2QX-9MRT-H4WZ";

    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-pairing-").FullName;
    private readonly DeviceIdentity _sender = DeviceIdentity.Create();
    private readonly DeviceIdentity _receiver = DeviceIdentity.Create();

    public void Dispose()
    {
        _sender.Dispose();
        _receiver.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task SameCode_PairsBothDevices_AndTheyCanThenTransferWithoutCode()
    {
        var (a, b) = MemoryTransport.CreatePair();
        var receiverSide = new TransferSession(b).PairAsync(_receiver, "Escritório", Code);
        // O remetente digitou o código sem hífens e em minúsculas.
        var senderSide = new TransferSession(a).PairAsync(_sender, "Notebook", "k7p2qx9mrth4wz");

        var receiverLearned = await receiverSide.WaitAsync(TimeSpan.FromSeconds(10));
        var senderLearned = await senderSide.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(_sender.PublicKey, receiverLearned.IdentityKey);
        Assert.Equal("Notebook", receiverLearned.Name);
        Assert.Equal(_sender.DeviceId, receiverLearned.DeviceId);
        Assert.Equal(_receiver.PublicKey, senderLearned.IdentityKey);
        Assert.Equal("Escritório", senderLearned.Name);

        // Com as identidades aprendidas, a transferência autenticada funciona.
        var package = Path.Combine(_dir, "package.zpp2");
        File.WriteAllBytes(package, new byte[100_000]);
        var header = new TransferHeader(Guid.NewGuid(), "f.bin", 100_000, 0);
        var (c, d) = MemoryTransport.CreatePair();
        var received = new TransferSession(d, authentication: Trusting(_receiver, receiverLearned))
            .ReceiveAsync(_ => Path.Combine(_dir, "recv.part"));
        await new TransferSession(c, authentication: Trusting(_sender, senderLearned))
            .SendAsync(package, header).WaitAsync(TimeSpan.FromSeconds(10));
        await received.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WrongCode_IsRejectedOnBothSides()
    {
        var (a, b) = MemoryTransport.CreatePair();
        var receiverSide = new TransferSession(b).PairAsync(_receiver, "PC", Code);
        var senderSide = new TransferSession(a).PairAsync(_sender, "PC", "K7P-2QX-9MRT-H4WY");

        var onReceiver = await Assert.ThrowsAsync<PeerNotTrustedException>(() => receiverSide.WaitAsync(TimeSpan.FromSeconds(10)));
        var onSender = await Assert.ThrowsAsync<PeerNotTrustedException>(() => senderSide.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("does not match", onReceiver.Message);
        Assert.Contains("does not match", onSender.Message);
    }

    [Fact]
    public async Task ManInTheMiddle_WithoutTheCode_CannotPair()
    {
        // A sinalização comprometida troca as chaves efêmeras e repassa as mensagens de pareamento.
        var (s, mitmToSender) = MemoryTransport.CreatePair();
        var (mitmToReceiver, r) = MemoryTransport.CreatePair();
        var fromSender = Listen(mitmToSender);
        var fromReceiver = Listen(mitmToReceiver);

        var receiverSide = new TransferSession(r).PairAsync(_receiver, "PC", Code);
        var senderSide = new TransferSession(s).PairAsync(_sender, "PC", Code);

        using var withSender = new KeyAgreement();
        using var withReceiver = new KeyAgreement();
        await mitmToSender.SendAsync(Message(0x01, withSender.PublicKey));
        await mitmToReceiver.SendAsync(Message(0x01, withReceiver.PublicKey));
        var senderCipher = new ChunkCipher(withSender.DeriveKey((await fromSender.ReadAsync())[1..].Span));
        var receiverCipher = new ChunkCipher(withReceiver.DeriveKey((await fromReceiver.ReadAsync())[1..].Span));

        var senderPairing = senderCipher.Decrypt((await fromSender.ReadAsync())[1..].Span);
        var receiverPairing = receiverCipher.Decrypt((await fromReceiver.ReadAsync())[1..].Span);
        await mitmToReceiver.SendAsync(Message(0x0A, receiverCipher.Encrypt(senderPairing)));
        await mitmToSender.SendAsync(Message(0x0A, senderCipher.Encrypt(receiverPairing)));

        await Assert.ThrowsAsync<PeerNotTrustedException>(() => receiverSide.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<PeerNotTrustedException>(() => senderSide.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task PeerClosingRightAfterConfirming_StillCompletesThePairing()
    {
        // Este lado recebe com atraso: o par conclui e fecha antes de o Done dele ser entregue aqui.
        var (a, b) = MemoryTransport.CreatePair();
        a.ReceiveDelay = TimeSpan.FromMilliseconds(150);
        var slowSide = new TransferSession(a).PairAsync(_sender, "Notebook", Code);
        var fastSide = new TransferSession(b).PairAsync(_receiver, "PC", Code);

        await fastSide.WaitAsync(TimeSpan.FromSeconds(10));
        b.Close();

        Assert.Equal(_receiver.DeviceId, (await slowSide.WaitAsync(TimeSpan.FromSeconds(10))).DeviceId);
    }

    [Fact]
    public async Task DeviceName_IsSanitized()
    {
        var (a, b) = MemoryTransport.CreatePair();
        var receiverSide = new TransferSession(b).PairAsync(_receiver, "PC", Code);
        var senderSide = new TransferSession(a).PairAsync(_sender, "  Note\r\nbook" + new string('x', 100), Code);

        var learned = await receiverSide.WaitAsync(TimeSpan.FromSeconds(10));
        await senderSide.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.StartsWith("Notebookxxx", learned.Name);
        Assert.Equal(64, learned.Name.Length);
    }

    [Fact]
    public async Task Runner_WrongCode_FailsWithoutRetrying()
    {
        var factory = new MemoryPeerLinkFactory();
        var receiver = new TransferRunner(factory, new TransferStore(Path.Combine(_dir, "r")), backoff: _ => TimeSpan.FromMilliseconds(50));
        var sender = new TransferRunner(factory, new TransferStore(Path.Combine(_dir, "s")), backoff: _ => TimeSpan.FromMilliseconds(50));

        var receiverSide = receiver.PairAsync("ROOM", false, _receiver, "PC", Code, CancellationToken.None);
        var senderSide = sender.PairAsync("ROOM", true, _sender, "PC", "AAA-AAA-AAAA-AAAA", CancellationToken.None);

        await Assert.ThrowsAsync<PeerNotTrustedException>(() => receiverSide.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<PeerNotTrustedException>(() => senderSide.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, factory.Pairs);
    }

    [Fact]
    public async Task Runner_PairsEvenWhenTheRunnerAuthenticatesTransfers()
    {
        // O runner do receive mode exige par confiável nas transferências, mas o pareamento roda sem isso.
        var factory = new MemoryPeerLinkFactory();
        var receiver = new TransferRunner(
            factory, new TransferStore(Path.Combine(_dir, "r")), authentication: new SessionAuthentication(_receiver, _ => false));
        var sender = new TransferRunner(factory, new TransferStore(Path.Combine(_dir, "s")));

        var receiverSide = receiver.PairAsync("ROOM", false, _receiver, "PC", Code, CancellationToken.None);
        var senderSide = sender.PairAsync("ROOM", true, _sender, "Notebook", Code, CancellationToken.None);

        Assert.Equal("Notebook", (await receiverSide.WaitAsync(TimeSpan.FromSeconds(10))).Name);
        Assert.Equal(_receiver.DeviceId, (await senderSide.WaitAsync(TimeSpan.FromSeconds(10))).DeviceId);
    }

    private static SessionAuthentication Trusting(DeviceIdentity self, PairedPeer peer)
        => new(self, key => key.AsSpan().SequenceEqual(peer.IdentityKey));

    private static ChannelReader<ReadOnlyMemory<byte>> Listen(MemoryTransport transport)
    {
        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        transport.MessageReceived += m => channel.Writer.TryWrite(m);
        return channel.Reader;
    }

    private static byte[] Message(byte type, byte[] payload) => [type, .. payload];
}
