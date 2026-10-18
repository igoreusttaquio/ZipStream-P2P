using Microsoft.AspNetCore.SignalR;
using ZipP2P.Contracts;

namespace ZipP2P.Server;

/// <summary>
/// HUB de sinalização: pareia dois clientes por um código de sala e retransmite
/// SDP e candidatos ICE entre eles. Não transporta os dados em si.
/// </summary>
public sealed class SignalingHub : Hub
{
    /// <summary>Entra em uma sala identificada pelo código; avisa os demais presentes.</summary>
    public async Task JoinRoom(string roomCode)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode).ConfigureAwait(false);
        await Clients.OthersInGroup(roomCode).SendAsync("PeerJoined").ConfigureAwait(false);
    }

    public Task SendDescription(string roomCode, SignalDescription description)
        => Clients.OthersInGroup(roomCode).SendAsync("ReceiveDescription", description);

    public Task SendCandidate(string roomCode, SignalCandidate candidate)
        => Clients.OthersInGroup(roomCode).SendAsync("ReceiveCandidate", candidate);

    public Task LeaveRoom(string roomCode)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, roomCode);
}
