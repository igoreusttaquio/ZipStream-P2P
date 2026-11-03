using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZipP2P.Contracts;
using ZipP2P.Core.Security;

namespace ZipP2P.P2P;

/// <summary>
/// Conexão com o servidor de sinalização para o receive mode: o receptor se registra (com a identidade
/// e, opcionalmente, o código) e recebe convites; quem envia convida um dispositivo para uma sala nova.
/// A sala em si segue pelo fluxo normal (<see cref="SignalingPeerLinkFactory"/>).
/// </summary>
public sealed class PresenceClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger _logger;
    private readonly string _serverUrl;
    private DeviceIdentity? _identity;
    private string? _lookupId;

    public PresenceClient(string serverUrl, ILogger? logger = null)
    {
        _serverUrl = serverUrl;
        _logger = logger ?? NullLogger.Instance;
        _connection = new HubConnectionBuilder()
            .WithUrl(serverUrl)
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        _connection.On<Invitation>("Invited", invitation =>
        {
            _logger.LogInformation("Invited to room {Room} ({Kind})", invitation.RoomCode, invitation.Kind);
            Invited?.Invoke(invitation);
        });
        _connection.Reconnecting += ex =>
        {
            _logger.LogWarning("Presence connection lost ({Message}); reconnecting", ex?.Message);
            ConnectionChanged?.Invoke(false);
            return Task.CompletedTask;
        };
        _connection.Reconnected += async _ =>
        {
            // O servidor esquece a presença quando a conexão cai: registra de novo.
            try
            {
                if (_identity is not null)
                    await RegisterAsync(CancellationToken.None).ConfigureAwait(false);
                ConnectionChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not register the device again after reconnecting");
            }
        };
    }

    /// <summary>Convite recebido (só chega depois de <see cref="RegisterAsync(DeviceIdentity, string?, CancellationToken)"/>).</summary>
    public event Action<Invitation>? Invited;

    /// <summary>A conexão caiu (false) ou voltou e o registro foi refeito (true).</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Connecting presence to {Url}", _serverUrl);
        try
        {
            await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SignalingException($"Could not reach the signaling server at {_serverUrl}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Registra este dispositivo para receber convites de transferência e, com <paramref name="receiverCode"/>,
    /// também convites de pareamento por esse código. A identidade continua sendo do chamador.
    /// </summary>
    public Task RegisterAsync(DeviceIdentity identity, string? receiverCode, CancellationToken cancellationToken = default)
    {
        _identity = identity;
        _lookupId = receiverCode is null ? null : ReceiverCode.LookupId(receiverCode);
        return RegisterAsync(cancellationToken);
    }

    /// <summary>Convida um dispositivo pareado para a sala. Lança <see cref="DeviceOfflineException"/> se ele não estiver em receive mode.</summary>
    public async Task InviteDeviceAsync(string deviceId, string roomCode, CancellationToken cancellationToken = default)
    {
        if (!await InvokeAsync<bool>("InviteDevice", [deviceId, roomCode], cancellationToken).ConfigureAwait(false))
            throw new DeviceOfflineException("The device is not online in receive mode.");
    }

    /// <summary>Convida o computador dono do código para parear. Lança <see cref="DeviceOfflineException"/> se ninguém estiver com esse código.</summary>
    public async Task InviteByCodeAsync(string receiverCode, string roomCode, CancellationToken cancellationToken = default)
    {
        var lookupId = ReceiverCode.LookupId(receiverCode);
        if (!await InvokeAsync<bool>("InviteByCode", [lookupId, roomCode], cancellationToken).ConfigureAwait(false))
            throw new DeviceOfflineException("No computer in receive mode has this code.");
    }

    /// <summary>Quais destes dispositivos estão em receive mode agora.</summary>
    public async Task<IReadOnlySet<string>> GetOnlineDevicesAsync(IEnumerable<string> deviceIds, CancellationToken cancellationToken = default)
        => (await InvokeAsync<string[]>("GetOnlineDevices", [deviceIds.ToArray()], cancellationToken).ConfigureAwait(false)).ToHashSet();

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var challenge = await InvokeAsync<byte[]>("GetPresenceChallenge", [], cancellationToken).ConfigureAwait(false);
        var signature = _identity!.SignPresenceChallenge(challenge);
        var deviceId = await InvokeAsync<string>(
            "RegisterPresence", [_identity.PublicKey, signature, _lookupId], cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Registered for receive mode as device {DeviceId}", deviceId);
    }

    private async Task<T> InvokeAsync<T>(string method, object?[] args, CancellationToken cancellationToken)
    {
        try
        {
            return await _connection.InvokeCoreAsync<T>(method, args, cancellationToken).ConfigureAwait(false);
        }
        catch (HubException ex)
        {
            throw new SignalingException(ex.Message, ex);
        }
    }

    /// <summary>Receive mode fica ligado por horas: tenta reconectar para sempre, com espera de até 30 s.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
            => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 5))));
    }
}

/// <summary>O dispositivo convidado não está em receive mode (app fechado, modo desligado ou sem rede).</summary>
public sealed class DeviceOfflineException : Exception
{
    public DeviceOfflineException(string message)
        : base(message)
    {
    }
}
