using System.Collections.Concurrent;
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

        return base.OnDisconnectedAsync(exception);
    }

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
