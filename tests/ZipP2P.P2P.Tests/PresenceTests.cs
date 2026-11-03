using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using ZipP2P.Contracts;
using ZipP2P.Core.Compression;
using ZipP2P.Core.Security;
using ZipP2P.P2P.Transfer;
using ZipP2P.Server;

namespace ZipP2P.P2P.Tests;

/// <summary>Presença e convites do receive mode, com o servidor de sinalização real em loopback.</summary>
public sealed class PresenceTests : IAsyncLifetime
{
    private const string Code = "K7P-2QX-9MRT-H4WZ";

    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-presence-").FullName;
    private readonly int _port = Random.Shared.Next(20000, 40000);
    private readonly DeviceIdentity _receiver = DeviceIdentity.Create();
    private readonly DeviceIdentity _sender = DeviceIdentity.Create();
    private readonly List<IAsyncDisposable> _clients = [];
    private WebApplication? _server;

    private string Url => $"http://127.0.0.1:{_port}/signaling";

    public async Task InitializeAsync()
    {
        _server = SignalingServer.Build($"http://127.0.0.1:{_port}");
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        await _server!.StopAsync();
        await _server.DisposeAsync();
        _receiver.Dispose();
        _sender.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task RegisteredDevice_ReceivesTheInvitation()
    {
        var receiver = await StartAsync();
        var invited = new TaskCompletionSource<Invitation>();
        receiver.Invited += i => invited.TrySetResult(i);
        await receiver.RegisterAsync(_receiver, receiverCode: null);

        await (await StartAsync()).InviteDeviceAsync(_receiver.DeviceId, "ROOM-1");

        Assert.Equal(new Invitation("ROOM-1", InvitationKind.Transfer), await invited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task OfflineDevice_IsReported()
    {
        var sender = await StartAsync();

        await Assert.ThrowsAsync<DeviceOfflineException>(() => sender.InviteDeviceAsync(_receiver.DeviceId, "ROOM"));
        await Assert.ThrowsAsync<DeviceOfflineException>(() => sender.InviteByCodeAsync(Code, "ROOM"));
    }

    [Fact]
    public async Task ReceiverCode_ReachesTheReceiver_ButOnlyWithTheRightCode()
    {
        var receiver = await StartAsync();
        var invited = new TaskCompletionSource<Invitation>();
        receiver.Invited += i => invited.TrySetResult(i);
        await receiver.RegisterAsync(_receiver, Code);
        var sender = await StartAsync();

        await Assert.ThrowsAsync<DeviceOfflineException>(() => sender.InviteByCodeAsync("K7P-2QX-9MRT-H4WY", "ROOM"));
        await sender.InviteByCodeAsync("k7p2qx9mrth4wz", "PAIR-1");

        Assert.Equal(new Invitation("PAIR-1", InvitationKind.Pairing), await invited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task OnlineDevices_FollowTheConnection()
    {
        var receiver = await StartAsync();
        await receiver.RegisterAsync(_receiver, receiverCode: null);
        var sender = await StartAsync();

        Assert.Equal([_receiver.DeviceId], await sender.GetOnlineDevicesAsync([_receiver.DeviceId, _sender.DeviceId]));

        await receiver.DisposeAsync();
        await WaitUntilAsync(async () => (await sender.GetOnlineDevicesAsync([_receiver.DeviceId])).Count == 0);
    }

    [Fact]
    public async Task NewRegistration_OfTheSameDevice_TakesOver()
    {
        var old = await StartAsync();
        await old.RegisterAsync(_receiver, receiverCode: null);
        var current = await StartAsync();
        var invited = new TaskCompletionSource<Invitation>();
        current.Invited += i => invited.TrySetResult(i);
        await current.RegisterAsync(_receiver, receiverCode: null);

        // A conexão antiga cair não pode tirar o registro novo.
        await old.DisposeAsync();
        await Task.Delay(300);
        await (await StartAsync()).InviteDeviceAsync(_receiver.DeviceId, "ROOM-2");

        Assert.Equal("ROOM-2", (await invited.Task.WaitAsync(TimeSpan.FromSeconds(10))).RoomCode);
    }

    [Fact]
    public async Task CannotRegister_AsAnotherDevice()
    {
        await using var attacker = new HubConnectionBuilder().WithUrl(Url).Build();
        await attacker.StartAsync();
        using var attackerIdentity = DeviceIdentity.Create();

        // Sem pedir o desafio.
        await Assert.ThrowsAsync<HubException>(() => attacker.InvokeAsync<string>(
            "RegisterPresence", _receiver.PublicKey, new byte[64], null));

        // Com a chave pública da vítima, mas assinando com outra identidade.
        var challenge = await attacker.InvokeAsync<byte[]>("GetPresenceChallenge");
        await Assert.ThrowsAsync<HubException>(() => attacker.InvokeAsync<string>(
            "RegisterPresence", _receiver.PublicKey, attackerIdentity.SignPresenceChallenge(challenge), null));

        // Reaproveitando uma assinatura válida de outro desafio (o desafio é de uso único).
        challenge = await attacker.InvokeAsync<byte[]>("GetPresenceChallenge");
        var signature = attackerIdentity.SignPresenceChallenge(challenge);
        Assert.Equal(attackerIdentity.DeviceId, await attacker.InvokeAsync<string>("RegisterPresence", attackerIdentity.PublicKey, signature, null));
        await Assert.ThrowsAsync<HubException>(() => attacker.InvokeAsync<string>(
            "RegisterPresence", attackerIdentity.PublicKey, signature, null));

        Assert.Empty(await (await StartAsync()).GetOnlineDevicesAsync([_receiver.DeviceId]));
    }

    [Fact]
    public async Task Invitations_AreRateLimited()
    {
        var sender = await StartAsync();
        for (var i = 0; i < SignalingHub.MaxInvitesPerMinute; i++)
            await Assert.ThrowsAsync<DeviceOfflineException>(() => sender.InviteDeviceAsync(_receiver.DeviceId, "R"));

        await Assert.ThrowsAsync<SignalingException>(() => sender.InviteDeviceAsync(_receiver.DeviceId, "R"));
    }

    [Fact]
    public async Task PairByCode_ThenSendWithoutCode_EndToEnd()
    {
        var factory = new SignalingPeerLinkFactory(Url);
        PairedPeer? receiverLearned = null;

        // Receptor em receive mode: pareia ou recebe conforme o convite.
        var receiverStore = new TransferStore(Path.Combine(_dir, "r"));
        var receiverRunner = new TransferRunner(
            factory, receiverStore,
            authentication: new SessionAuthentication(_receiver, key => receiverLearned?.IdentityKey.AsSpan().SequenceEqual(key) == true));
        var paired = new TaskCompletionSource();
        var received = new TaskCompletionSource<IncomingTransfer>();
        var receiver = await StartAsync();
        receiver.Invited += invitation => _ = Task.Run(async () =>
        {
            try
            {
                if (invitation.Kind == InvitationKind.Pairing)
                {
                    receiverLearned = await receiverRunner.PairAsync(invitation.RoomCode, false, _receiver, "Escritório", Code, CancellationToken.None);
                    paired.TrySetResult();
                }
                else
                {
                    received.TrySetResult(await receiverRunner.ReceiveAsync(invitation.RoomCode, Path.Combine(_dir, "dl"), null, CancellationToken.None));
                }
            }
            catch (Exception ex)
            {
                paired.TrySetException(ex);
                received.TrySetException(ex);
            }
        });
        await receiver.RegisterAsync(_receiver, Code);

        // Remetente: digita o código uma vez para parear.
        var sender = await StartAsync();
        var senderStore = new TransferStore(Path.Combine(_dir, "s"));
        await sender.InviteByCodeAsync(Code, "PAIR-E2E");
        var senderLearned = await new TransferRunner(factory, senderStore)
            .PairAsync("PAIR-E2E", true, _sender, "Notebook", Code, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));
        await paired.Task.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal("Escritório", senderLearned.Name);
        Assert.Equal("Notebook", receiverLearned!.Name);

        // Depois, envia sem código: convite pelo id do dispositivo + sessão autenticada.
        var source = Path.Combine(_dir, "doc.bin");
        var data = new byte[2 * 1024 * 1024];
        new Random(9).NextBytes(data);
        await File.WriteAllBytesAsync(source, data);
        var id = Guid.NewGuid();
        var type = await TransferPackage.CreateAsync(source, senderStore.PackagePath(id), new CompressionOptions { Level = 1 });
        var outgoing = new OutgoingTransfer(id, source, "doc.bin", type, new FileInfo(senderStore.PackagePath(id)).Length, "SEND-E2E", DateTimeOffset.Now);
        senderStore.Save(outgoing);

        await sender.InviteDeviceAsync(senderLearned.DeviceId, outgoing.RoomCode);
        await new TransferRunner(
                factory, senderStore,
                authentication: new SessionAuthentication(_sender, key => senderLearned.IdentityKey.AsSpan().SequenceEqual(key)))
            .SendAsync(outgoing, null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));
        var incoming = await received.Task.WaitAsync(TimeSpan.FromMinutes(1));

        var extracted = await TransferPackage.ExtractAsync(
            receiverStore.PartialPath(incoming.Id), incoming.FileName, incoming.ContentType, incoming.DestinationDir);
        Assert.Equal(data, await File.ReadAllBytesAsync(extracted));
    }

    private async Task<PresenceClient> StartAsync()
    {
        var client = new PresenceClient(Url);
        _clients.Add(client);
        await client.StartAsync();
        return client;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(100);
        }
    }
}
