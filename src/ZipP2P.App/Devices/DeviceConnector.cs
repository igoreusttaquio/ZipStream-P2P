using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZipP2P.P2P;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.Devices;

/// <summary>
/// Lado de quem envia para dispositivos em receive mode: parear digitando o código do receptor,
/// convidar um dispositivo pareado para uma sala e saber quais estão online. Cada operação abre a
/// própria conexão de presença (só quem recebe precisa ficar conectado o tempo todo).
/// </summary>
public sealed class DeviceConnector
{
    private readonly DeviceProfile _profile;
    private readonly TrustedDeviceStore _trusted;
    private readonly Func<PresenceClient> _createPresence;
    private readonly Func<TransferRunner> _createRunner;
    private readonly Func<string> _createRoom;
    private readonly ILogger _logger;

    public DeviceConnector(
        DeviceProfile profile,
        TrustedDeviceStore trusted,
        Func<PresenceClient> createPresence,
        Func<TransferRunner> createRunner,
        Func<string> createRoom,
        ILogger? logger = null)
    {
        _profile = profile;
        _trusted = trusted;
        _createPresence = createPresence;
        _createRunner = createRunner;
        _createRoom = createRoom;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Pareia com o computador dono de <paramref name="receiverCode"/> e salva os dois lados como confiáveis
    /// (este aqui guarda o outro; o outro guarda este). Lança <see cref="DeviceOfflineException"/> se o
    /// código não estiver online e <see cref="PeerNotTrustedException"/> se a prova do código falhar.
    /// </summary>
    public async Task<TrustedDevice> PairAsync(string receiverCode, CancellationToken cancellationToken = default)
    {
        var room = _createRoom();
        await using (var presence = _createPresence())
        {
            await presence.StartAsync(cancellationToken).ConfigureAwait(false);
            await presence.InviteByCodeAsync(receiverCode, room, cancellationToken).ConfigureAwait(false);
        }

        using var identity = _profile.LoadIdentity();
        var peer = await _createRunner()
            .PairAsync(room, asOfferer: true, identity, _profile.DeviceName, receiverCode, cancellationToken)
            .ConfigureAwait(false);
        var device = _trusted.Trust(peer.IdentityKey, peer.Name);
        _logger.LogInformation("Paired with {Name} ({DeviceId})", device.Name, device.DeviceId);
        return device;
    }

    /// <summary>Convida <paramref name="device"/> para uma sala nova e a devolve. Lança <see cref="DeviceOfflineException"/> se estiver offline.</summary>
    public async Task<string> InviteAsync(TrustedDevice device, CancellationToken cancellationToken = default)
    {
        var room = _createRoom();
        await InviteAsync(device, room, cancellationToken).ConfigureAwait(false);
        return room;
    }

    /// <summary>Convida <paramref name="device"/> para a sala <paramref name="room"/> (ex.: retomar um envio).</summary>
    public async Task InviteAsync(TrustedDevice device, string room, CancellationToken cancellationToken = default)
    {
        await using var presence = _createPresence();
        await presence.StartAsync(cancellationToken).ConfigureAwait(false);
        await presence.InviteDeviceAsync(device.DeviceId, room, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Invited {Name} to room {Room}", device.Name, room);
    }

    /// <summary>Ids dos dispositivos confiáveis que estão em receive mode agora.</summary>
    public async Task<IReadOnlySet<string>> GetOnlineAsync(CancellationToken cancellationToken = default)
    {
        var ids = _trusted.List().Select(d => d.DeviceId).ToArray();
        if (ids.Length == 0)
            return new HashSet<string>();

        await using var presence = _createPresence();
        await presence.StartAsync(cancellationToken).ConfigureAwait(false);
        return await presence.GetOnlineDevicesAsync(ids, cancellationToken).ConfigureAwait(false);
    }
}
