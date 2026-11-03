using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZipP2P.Contracts;
using ZipP2P.Core.Security;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.Devices;

public enum ReceiveModeEventKind
{
    /// <summary>Um dispositivo pareou usando o código deste computador.</summary>
    Paired,

    /// <summary>Um dispositivo pareado começou a enviar.</summary>
    Receiving,

    /// <summary>Recebido e extraído em <see cref="ReceiveModeEvent.Path"/>.</summary>
    Received,

    Failed,
}

/// <summary>Algo que aconteceu no receive mode (para a tela, a bandeja e o log).</summary>
/// <param name="Name">Arquivo/pasta recebido ou, em <see cref="ReceiveModeEventKind.Paired"/>, o nome do dispositivo.</param>
/// <param name="DeviceName">Dispositivo que enviou ou pareou, se conhecido.</param>
public sealed record ReceiveModeEvent(
    ReceiveModeEventKind Kind, string Name, string? DeviceName, string? Path = null, string? Error = null);

/// <summary>
/// Receive mode: mantém este computador online no servidor de sinalização e atende os convites sem
/// ninguém na frente da tela. Pareamentos pelo código do receptor são aceitos (quem tem o código pode
/// parear); transferências só de dispositivos confiáveis, uma por vez, extraídas na pasta de destino.
/// </summary>
public sealed class ReceiveModeService : IAsyncDisposable
{
    /// <summary>Quanto esperar o remetente aparecer na sala depois do convite.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Quanto esperar o remetente voltar quando a conexão cai no meio.</summary>
    public static readonly TimeSpan ReconnectTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    private readonly DeviceProfile _profile;
    private readonly TrustedDeviceStore _trusted;
    private readonly TransferStore _store;
    private readonly Func<PresenceClient> _createPresence;
    private readonly Func<SessionAuthentication?, TransferRunner> _createRunner;
    private readonly Func<string> _downloadDirectory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _seenRooms = new();

    private CancellationTokenSource? _cts;
    private PresenceClient? _presence;
    private DeviceIdentity? _identity;
    private Channel<Invitation>? _queue;
    private Task _background = Task.CompletedTask;

    public ReceiveModeService(
        DeviceProfile profile,
        TrustedDeviceStore trusted,
        TransferStore store,
        Func<PresenceClient> createPresence,
        Func<SessionAuthentication?, TransferRunner> createRunner,
        Func<string> downloadDirectory,
        ILogger? logger = null)
    {
        _profile = profile;
        _trusted = trusted;
        _store = store;
        _createPresence = createPresence;
        _createRunner = createRunner;
        _downloadDirectory = downloadDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsRunning => _cts is not null;

    /// <summary>Conectado e registrado: pode receber convites agora.</summary>
    public bool IsOnline { get; private set; }

    /// <summary><see cref="IsRunning"/> ou <see cref="IsOnline"/> mudou.</summary>
    public event Action? StateChanged;

    public event Action<ReceiveModeEvent>? Activity;

    /// <summary>Progresso do recebimento em andamento.</summary>
    public event Action<TransferProgress>? Progress;

    /// <summary>
    /// Liga o receive mode. Retorna logo: a conexão com o servidor é feita em segundo plano e tentada de
    /// novo (com espera crescente) enquanto o servidor não responder.
    /// </summary>
    public void Start()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _identity = _profile.LoadIdentity();
        _queue = Channel.CreateUnbounded<Invitation>(new UnboundedChannelOptions { SingleReader = true });
        var token = _cts.Token;
        _background = Task.WhenAll(
            Task.Run(() => KeepConnectedAsync(token)),
            Task.Run(() => ProcessTransfersAsync(_queue.Reader, token)));
        _logger.LogInformation("Receive mode started");
        StateChanged?.Invoke();
    }

    /// <summary>Desliga: sai do servidor e interrompe o recebimento em andamento (fica pendente para retomar).</summary>
    public async Task StopAsync()
    {
        if (_cts is not { } cts)
            return;

        _cts = null;
        await cts.CancelAsync().ConfigureAwait(false);
        _queue?.Writer.TryComplete();
        try
        {
            await _background.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (_presence is not null)
            await _presence.DisposeAsync().ConfigureAwait(false);
        _presence = null;
        _identity?.Dispose();
        _identity = null;
        cts.Dispose();
        IsOnline = false;
        _logger.LogInformation("Receive mode stopped");
        StateChanged?.Invoke();
    }

    /// <summary>Troca o código; se estiver ligado, registra o novo no servidor (o antigo deixa de funcionar na hora).</summary>
    public async Task<string> RegenerateCodeAsync(CancellationToken cancellationToken = default)
    {
        var code = _profile.RegenerateReceiverCode();
        if (_presence is { IsConnected: true } presence && _identity is { } identity)
            await presence.RegisterAsync(identity, code, cancellationToken).ConfigureAwait(false);
        return code;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task KeepConnectedAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var presence = _createPresence();
            presence.Invited += OnInvited;
            presence.ConnectionChanged += online => SetOnline(online);
            try
            {
                await presence.StartAsync(cancellationToken).ConfigureAwait(false);
                await presence.RegisterAsync(_identity!, _profile.ReceiverCode, cancellationToken).ConfigureAwait(false);
                _presence = presence;
                SetOnline(true);
                return; // daqui em diante o PresenceClient reconecta e registra de novo sozinho
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await presence.DisposeAsync().ConfigureAwait(false);
                var delay = TimeSpan.FromSeconds(Math.Min(MaxRetryDelay.TotalSeconds, 5 * Math.Pow(2, Math.Min(attempt++, 4))));
                _logger.LogWarning("Receive mode could not connect ({Message}); retrying in {Seconds:F0}s", ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await presence.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private void OnInvited(Invitation invitation)
    {
        // O servidor pode repetir um convite (ex.: o remetente tentou de novo); a mesma sala só é atendida uma vez.
        if (_cts is not { } cts || !_seenRooms.TryAdd(invitation.RoomCode, 0))
            return;

        if (invitation.Kind == InvitationKind.Pairing)
            _ = Task.Run(() => PairAsync(invitation.RoomCode, cts.Token));
        else
            _queue?.Writer.TryWrite(invitation);
    }

    private async Task PairAsync(string roomCode, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(JoinTimeout);
        try
        {
            var peer = await _createRunner(null)
                .PairAsync(roomCode, asOfferer: false, _identity!, _profile.DeviceName, _profile.ReceiverCode, timeout.Token)
                .ConfigureAwait(false);
            _trusted.Trust(peer.IdentityKey, peer.Name);
            _logger.LogInformation("Paired with {Name} ({DeviceId}) through the receiver code", peer.Name, peer.DeviceId);
            Activity?.Invoke(new ReceiveModeEvent(ReceiveModeEventKind.Paired, peer.Name, peer.Name));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Pairing request failed: {Message}", ex.Message);
            Activity?.Invoke(new ReceiveModeEvent(ReceiveModeEventKind.Failed, string.Empty, null, Error: ex.Message));
        }
    }

    private async Task ProcessTransfersAsync(ChannelReader<Invitation> invitations, CancellationToken cancellationToken)
    {
        await foreach (var invitation in invitations.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await ReceiveAsync(invitation.RoomCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(string roomCode, CancellationToken cancellationToken)
    {
        // Prazo para o remetente aparecer (e para voltar, se cair); sem prazo enquanto os dados fluem.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(JoinTimeout);
        TrustedDevice? sender = null;
        IncomingTransfer? incoming = null;

        var authentication = new SessionAuthentication(_identity!, key =>
        {
            sender = _trusted.Find(DeviceIdentity.ComputeDeviceId(key));
            return sender is not null && sender.PublicKey.AsSpan().SequenceEqual(key);
        });
        var runner = _createRunner(authentication);
        var announced = false;
        runner.StatusChanged += status =>
        {
            switch (status.Stage)
            {
                case TransferStage.Transferring:
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    break;
                case TransferStage.Reconnecting:
                    deadline.CancelAfter(ReconnectTimeout);
                    break;
            }
        };
        var progress = new Progress<TransferProgress>(p =>
        {
            if (!announced)
            {
                announced = true;
                Activity?.Invoke(new ReceiveModeEvent(ReceiveModeEventKind.Receiving, string.Empty, sender?.Name));
            }

            Progress?.Invoke(p);
        });

        try
        {
            incoming = await runner.ReceiveAsync(roomCode, _downloadDirectory(), progress, deadline.Token).ConfigureAwait(false);
            var path = await TransferPackage.ExtractAsync(
                _store.PartialPath(incoming.Id), incoming.FileName, incoming.ContentType, incoming.DestinationDir,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _store.Delete(incoming);
            _logger.LogInformation("Received {File} from {Device} into {Path}", incoming.FileName, sender?.Name, path);
            Activity?.Invoke(new ReceiveModeEvent(ReceiveModeEventKind.Received, incoming.FileName, sender?.Name, path));
        }
        catch (InvalidDataException ex) when (incoming is not null)
        {
            // Pacote corrompido: não adianta retomar.
            _store.Delete(incoming);
            Report(ex, sender);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Interrompido: o recebimento fica pendente e é retomado quando o remetente enviar de novo.
            Report(ex, sender);
        }
    }

    private void Report(Exception ex, TrustedDevice? sender)
    {
        var message = ex is OperationCanceledException ? "The other device did not connect in time." : ex.Message;
        _logger.LogWarning("Receive mode transfer failed: {Message}", message);
        Activity?.Invoke(new ReceiveModeEvent(ReceiveModeEventKind.Failed, string.Empty, sender?.Name, Error: message));
    }

    private void SetOnline(bool online)
    {
        if (IsOnline == online)
            return;

        IsOnline = online;
        StateChanged?.Invoke();
    }
}
