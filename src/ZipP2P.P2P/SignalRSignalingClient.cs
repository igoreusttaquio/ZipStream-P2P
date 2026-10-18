using Microsoft.AspNetCore.SignalR.Client;
using ZipP2P.Contracts;

namespace ZipP2P.P2P;

/// <summary>Cliente de sinalização baseado em SignalR.</summary>
public sealed class SignalRSignalingClient : ISignalingClient
{
    private readonly HubConnection _connection;

    public SignalRSignalingClient(string serverUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(serverUrl)
            .Build();

        _connection.On("PeerJoined", () => PeerJoined?.Invoke());
        _connection.On<SignalDescription>("ReceiveDescription", d => DescriptionReceived?.Invoke(d));
        _connection.On<SignalCandidate>("ReceiveCandidate", c => CandidateReceived?.Invoke(c));
    }

    public event Action? PeerJoined;
    public event Action<SignalDescription>? DescriptionReceived;
    public event Action<SignalCandidate>? CandidateReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _connection.StartAsync(cancellationToken);

    public Task JoinRoomAsync(string roomCode, CancellationToken cancellationToken = default)
        => _connection.InvokeAsync("JoinRoom", roomCode, cancellationToken);

    public Task LeaveRoomAsync(string roomCode, CancellationToken cancellationToken = default)
        => _connection.InvokeAsync("LeaveRoom", roomCode, cancellationToken);

    public Task SendDescriptionAsync(string roomCode, SignalDescription description, CancellationToken cancellationToken = default)
        => _connection.InvokeAsync("SendDescription", roomCode, description, cancellationToken);

    public Task SendCandidateAsync(string roomCode, SignalCandidate candidate, CancellationToken cancellationToken = default)
        => _connection.InvokeAsync("SendCandidate", roomCode, candidate, cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
