using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using ZipP2P.Contracts;

namespace ZipP2P.Server;

/// <summary>
/// HUB de sinalização: pareia dois clientes por um código de sala e retransmite
/// SDP e candidatos ICE entre eles. Não transporta os dados em si.
/// </summary>
public sealed class SignalingHub : Hub
{
    /// <summary>Uma transferência envolve exatamente dois dispositivos.</summary>
    public const int MaxPeersPerRoom = 2;

    // Membros de cada sala (connectionId). Estático: o hub é transitório (uma instância por chamada).
    private static readonly ConcurrentDictionary<string, HashSet<string>> Rooms = new();

    /// <summary>
    /// Entra em uma sala identificada pelo código. Avisa os demais presentes e, se o par já
    /// estava na sala (ex.: reconexão), avisa também quem acabou de entrar.
    /// </summary>
    public async Task JoinRoom(string roomCode)
    {
        bool peerAlreadyThere;
        while (true)
        {
            var members = Rooms.GetOrAdd(roomCode, _ => new HashSet<string>());
            lock (members)
            {
                // A sala pode ter sido removida (ficou vazia) entre o GetOrAdd e o lock.
                if (!Rooms.TryGetValue(roomCode, out var current) || !ReferenceEquals(current, members))
                    continue;

                if (!members.Contains(Context.ConnectionId) && members.Count >= MaxPeersPerRoom)
                    throw new HubException("This transfer code is already in use by two devices.");

                members.Add(Context.ConnectionId);
                peerAlreadyThere = members.Count > 1;
                break;
            }
        }

        Context.Items["room"] = roomCode;
        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode).ConfigureAwait(false);
        await Clients.OthersInGroup(roomCode).SendAsync("PeerJoined").ConfigureAwait(false);

        if (peerAlreadyThere)
            await Clients.Caller.SendAsync("PeerJoined").ConfigureAwait(false);
    }

    public Task SendDescription(string roomCode, SignalDescription description)
        => Clients.OthersInGroup(roomCode).SendAsync("ReceiveDescription", description);

    public Task SendCandidate(string roomCode, SignalCandidate candidate)
        => Clients.OthersInGroup(roomCode).SendAsync("ReceiveCandidate", candidate);

    public async Task LeaveRoom(string roomCode)
    {
        Leave(roomCode, Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomCode).ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("room", out var room) && room is string roomCode)
            Leave(roomCode, Context.ConnectionId);

        RemovePresence();
        return base.OnDisconnectedAsync(exception);
    }

    // ---- Presença (receive mode) ----
    // Um dispositivo em receive mode fica conectado e registrado; quem quer enviar o convida para uma
    // sala nova, e dali em diante o fluxo é o mesmo das salas acima. O hub não decide confiança: os
    // dispositivos se autenticam entre si na sessão (identidade assinada / código de pareamento).

    /// <summary>Máximo de convites por conexão a cada minuto (evita uso como canal de spam).</summary>
    public const int MaxInvitesPerMinute = 30;

    private const int MaxRoomCodeLength = 64;
    private const int MaxOnlineQuery = 100;

    // Endereço -> conexão: "device:{id}" (provado por assinatura) e "code:{lookupId}" (pareamento).
    private static readonly ConcurrentDictionary<string, string> Present = new();

    /// <summary>Primeiro passo do registro: um desafio aleatório, de uso único, para o dispositivo assinar.</summary>
    public byte[] GetPresenceChallenge()
    {
        var challenge = RandomNumberGenerator.GetBytes(32);
        Context.Items["challenge"] = challenge;
        return challenge;
    }

    /// <summary>
    /// Registra esta conexão como o dispositivo dono de <paramref name="publicKey"/> (e, se informado, como
    /// o receptor do código cujo endereço é <paramref name="lookupId"/>). Um novo registro do mesmo
    /// dispositivo (ex.: reconexão) substitui o anterior. Retorna o id do dispositivo.
    /// </summary>
    public string RegisterPresence(byte[] publicKey, byte[] signature, string? lookupId)
    {
        if (!Context.Items.Remove("challenge", out var stored) || stored is not byte[] challenge)
            throw new HubException("Request a presence challenge first.");
        if (!Presence.VerifyChallenge(publicKey, challenge, signature))
            throw new HubException("Invalid device signature.");

        RemovePresence();
        var deviceId = Presence.ComputeDeviceId(publicKey);
        var addresses = new List<string> { DeviceAddress(deviceId) };
        if (!string.IsNullOrEmpty(lookupId))
        {
            if (lookupId.Length != 64 || !lookupId.All(char.IsAsciiHexDigitLower))
                throw new HubException("Invalid receiver code address.");
            addresses.Add(CodeAddress(lookupId));
        }

        foreach (var address in addresses)
            Present[address] = Context.ConnectionId;
        Context.Items["presence"] = addresses;
        return deviceId;
    }

    /// <summary>Convida um dispositivo pareado para uma transferência. Retorna false se ele estiver offline.</summary>
    public Task<bool> InviteDevice(string deviceId, string roomCode)
        => InviteAsync(DeviceAddress(deviceId), new Invitation(roomCode, InvitationKind.Transfer));

    /// <summary>Convida o receptor do código (pelo endereço derivado dele) para parear. Retorna false se estiver offline.</summary>
    public Task<bool> InviteByCode(string lookupId, string roomCode)
        => InviteAsync(CodeAddress(lookupId), new Invitation(roomCode, InvitationKind.Pairing));

    /// <summary>Quais destes dispositivos estão em receive mode agora.</summary>
    public string[] GetOnlineDevices(string[] deviceIds)
        => deviceIds.Take(MaxOnlineQuery).Where(id => Present.ContainsKey(DeviceAddress(id))).ToArray();

    private async Task<bool> InviteAsync(string address, Invitation invitation)
    {
        if (string.IsNullOrWhiteSpace(invitation.RoomCode) || invitation.RoomCode.Length > MaxRoomCodeLength)
            throw new HubException("Invalid room code.");
        CountInvite();

        if (!Present.TryGetValue(address, out var connectionId))
            return false;

        await Clients.Client(connectionId).SendAsync("Invited", invitation).ConfigureAwait(false);
        return true;
    }

    private void CountInvite()
    {
        var now = DateTimeOffset.UtcNow;
        var (windowStart, count) = Context.Items.TryGetValue("invites", out var value) && value is (DateTimeOffset start, int n)
            ? (start, n)
            : (now, 0);
        if (now - windowStart >= TimeSpan.FromMinutes(1))
            (windowStart, count) = (now, 0);
        if (++count > MaxInvitesPerMinute)
            throw new HubException("Too many invitations; try again in a minute.");
        Context.Items["invites"] = (windowStart, count);
    }

    private void RemovePresence()
    {
        if (!Context.Items.Remove("presence", out var value) || value is not List<string> addresses)
            return;

        // Só remove se o endereço ainda apontar para esta conexão (outra pode ter se registrado depois).
        foreach (var address in addresses)
            Present.TryRemove(new KeyValuePair<string, string>(address, Context.ConnectionId));
    }

    private static string DeviceAddress(string deviceId) => "device:" + deviceId;

    private static string CodeAddress(string lookupId) => "code:" + lookupId;

    private static void Leave(string roomCode, string connectionId)
    {
        if (!Rooms.TryGetValue(roomCode, out var members))
            return;

        lock (members)
        {
            members.Remove(connectionId);
            if (members.Count == 0)
                Rooms.TryRemove(new KeyValuePair<string, HashSet<string>>(roomCode, members));
        }
    }
}
