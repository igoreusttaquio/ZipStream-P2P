using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using ZipP2P.App.Devices;
using ZipP2P.Core.Compression;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;
using ZipP2P.Server;

namespace ZipP2P.App.Tests;

/// <summary>
/// Receive mode de ponta a ponta: dois "computadores" (cada um com banco, identidade e pastas próprios)
/// falando pelo servidor de sinalização real e WebRTC em loopback.
/// </summary>
public sealed class ReceiveModeTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-receivemode-").FullName;
    private readonly int _port = Random.Shared.Next(20000, 40000);
    private WebApplication? _server;
    private Computer _receiver = null!;
    private Computer _sender = null!;

    private string Url => $"http://127.0.0.1:{_port}/signaling";

    public async Task InitializeAsync()
    {
        _server = SignalingServer.Build($"http://127.0.0.1:{_port}");
        await _server.StartAsync();
        _receiver = new Computer(Path.Combine(_dir, "receiver"), Url, "Escritório");
        _sender = new Computer(Path.Combine(_dir, "sender"), Url, "Notebook");
    }

    public async Task DisposeAsync()
    {
        await _receiver.ReceiveMode.DisposeAsync();
        await _sender.ReceiveMode.DisposeAsync();
        await _server!.StopAsync();
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task PairWithTheCode_ThenSendWithoutCode_IsReceivedAndExtracted()
    {
        var events = _receiver.Record();
        await _receiver.StartOnlineAsync();

        // Pareamento: o remetente digita o código do receptor.
        var receiverDevice = await _sender.Connector.PairAsync(_receiver.Profile.ReceiverCode).WaitAsync(Timeout);
        await events.WaitForAsync(ReceiveModeEventKind.Paired);
        Assert.Equal("Escritório", receiverDevice.Name);
        Assert.Equal("Notebook", Assert.Single(_receiver.Trusted.List()).Name);

        // Envio sem código: convite pelo dispositivo + sessão autenticada.
        var data = await _sender.SendAsync(receiverDevice, "relatorio.bin");

        var received = await events.WaitForAsync(ReceiveModeEventKind.Received);
        Assert.Equal("relatorio.bin", received.Name);
        Assert.Equal("Notebook", received.DeviceName);
        Assert.Equal(Path.Combine(_receiver.Downloads, "relatorio.bin"), received.Path);
        Assert.Equal(data, await File.ReadAllBytesAsync(received.Path!));
        Assert.Empty(_receiver.Store.ListIncoming());
        Assert.Empty(_sender.Store.ListOutgoing());
    }

    [Fact]
    public async Task UnpairedSender_IsRefused_AndNothingIsWritten()
    {
        var events = _receiver.Record();
        await _receiver.StartOnlineAsync();

        // O remetente conhece a identidade do receptor, mas o receptor nunca pareou com ele.
        using var receiverIdentity = _receiver.Profile.LoadIdentity();
        var receiverDevice = _sender.Trusted.Trust(receiverIdentity.PublicKey, "Escritório");

        await Assert.ThrowsAsync<TransferAbortedException>(() => _sender.SendAsync(receiverDevice, "x.bin"));

        await events.WaitForAsync(ReceiveModeEventKind.Failed);
        Assert.Empty(Directory.Exists(_receiver.Downloads) ? Directory.GetFiles(_receiver.Downloads) : []);
        Assert.Empty(_receiver.Store.ListIncoming());
    }

    [Fact]
    public async Task NewCode_ReplacesTheOldOne_ButKeepsPairedDevices()
    {
        await _receiver.StartOnlineAsync();
        var oldCode = _receiver.Profile.ReceiverCode;
        var paired = await _sender.Connector.PairAsync(oldCode).WaitAsync(Timeout);

        var newCode = await _receiver.ReceiveMode.RegenerateCodeAsync();

        await Assert.ThrowsAsync<DeviceOfflineException>(() => new Computer(Path.Combine(_dir, "third"), Url, "Outro").Connector.PairAsync(oldCode));
        Assert.NotEqual(oldCode, newCode);
        Assert.Contains(paired.DeviceId, await _sender.Connector.GetOnlineAsync());
    }

    [Fact]
    public async Task Stopping_TakesTheComputerOffline()
    {
        await _receiver.StartOnlineAsync();
        await _sender.Connector.PairAsync(_receiver.Profile.ReceiverCode).WaitAsync(Timeout);
        Assert.Single(await _sender.Connector.GetOnlineAsync());

        await _receiver.ReceiveMode.StopAsync();

        Assert.False(_receiver.ReceiveMode.IsRunning);
        Assert.Empty(await _sender.Connector.GetOnlineAsync());
        await Assert.ThrowsAsync<DeviceOfflineException>(() => _sender.Connector.InviteAsync(_sender.Trusted.List()[0]));
    }

    [Fact]
    public async Task ServerDown_KeepsTrying_UntilItComesBack()
    {
        // Liga com o servidor fora do ar: fica "conectando" e entra assim que ele volta.
        await _server!.StopAsync();
        _receiver.ReceiveMode.Start();
        await Task.Delay(500);
        Assert.True(_receiver.ReceiveMode.IsRunning);
        Assert.False(_receiver.ReceiveMode.IsOnline);

        await _server.DisposeAsync();
        _server = SignalingServer.Build($"http://127.0.0.1:{_port}");
        await _server.StartAsync();

        await WaitUntilAsync(() => _receiver.ReceiveMode.IsOnline, TimeSpan.FromSeconds(30));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(100);
        }
    }

    /// <summary>Um computador com o app: perfil, confiáveis, transferências e os serviços de dispositivos.</summary>
    private sealed class Computer
    {
        public Computer(string root, string url, string name)
        {
            Directory.CreateDirectory(root);
            Downloads = Path.Combine(root, "Downloads");
            Profile = new DeviceProfile(new SettingsStore(Path.Combine(root, "settings.db"))) { DeviceName = name };
            Trusted = new TrustedDeviceStore(Path.Combine(root, "settings.db"));
            Store = new TransferStore(Path.Combine(root, "transfers"));
            var factory = new SignalingPeerLinkFactory(url);
            ReceiveMode = new ReceiveModeService(
                Profile, Trusted, Store, () => new PresenceClient(url),
                auth => new TransferRunner(factory, Store, authentication: auth), () => Downloads);
            Connector = new DeviceConnector(
                Profile, Trusted, () => new PresenceClient(url), () => new TransferRunner(factory, Store), TransferServices.GenerateInviteRoom);
            Factory = factory;
        }

        public string Downloads { get; }

        public DeviceProfile Profile { get; }

        public TrustedDeviceStore Trusted { get; }

        public TransferStore Store { get; }

        public ReceiveModeService ReceiveMode { get; }

        public DeviceConnector Connector { get; }

        public SignalingPeerLinkFactory Factory { get; }

        public async Task StartOnlineAsync()
        {
            ReceiveMode.Start();
            await WaitUntilAsync(() => ReceiveMode.IsOnline, TimeSpan.FromSeconds(15));
        }

        public EventRecorder Record()
        {
            var recorder = new EventRecorder();
            ReceiveMode.Activity += recorder.Add;
            return recorder;
        }

        /// <summary>Como o "Enviar para dispositivo" da aba Enviar: convida e envia com sessão autenticada.</summary>
        public async Task<byte[]> SendAsync(TrustedDevice target, string fileName)
        {
            var source = Path.Combine(Path.GetDirectoryName(Downloads)!, fileName);
            var data = new byte[1024 * 1024 + 17];
            new Random(fileName.Length).NextBytes(data);
            await File.WriteAllBytesAsync(source, data);

            var id = Guid.NewGuid();
            var type = await TransferPackage.CreateAsync(source, Store.PackagePath(id), new CompressionOptions { Level = 1 });
            var room = TransferServices.GenerateInviteRoom();
            var outgoing = new OutgoingTransfer(
                id, source, fileName, type, new FileInfo(Store.PackagePath(id)).Length, room, DateTimeOffset.Now, target.DeviceId);
            Store.Save(outgoing);

            await Connector.InviteAsync(target, room);
            using var identity = Profile.LoadIdentity();
            await new TransferRunner(Factory, Store, authentication: DeviceTrust.OnlyDevice(identity, target))
                .SendAsync(outgoing, null, CancellationToken.None).WaitAsync(Timeout);
            return data;
        }
    }

    private sealed class EventRecorder
    {
        private readonly List<ReceiveModeEvent> _events = [];

        public void Add(ReceiveModeEvent e)
        {
            lock (_events)
                _events.Add(e);
        }

        public async Task<ReceiveModeEvent> WaitForAsync(ReceiveModeEventKind kind)
        {
            ReceiveModeEvent? found = null;
            await WaitUntilAsync(
                () =>
                {
                    lock (_events)
                        return (found = _events.FirstOrDefault(e => e.Kind == kind)) is not null;
                },
                Timeout);
            return found!;
        }
    }
}
