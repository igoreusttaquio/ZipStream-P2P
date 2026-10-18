using ZipP2P.Contracts;

namespace ZipP2P.P2P;

/// <summary>Cliente de sinalização: conecta ao servidor e troca SDP/candidatos com o par.</summary>
public interface ISignalingClient : IAsyncDisposable
{
    event Action? PeerJoined;
    event Action<SignalDescription>? DescriptionReceived;
    event Action<SignalCandidate>? CandidateReceived;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task JoinRoomAsync(string roomCode, CancellationToken cancellationToken = default);
    Task LeaveRoomAsync(string roomCode, CancellationToken cancellationToken = default);
    Task SendDescriptionAsync(string roomCode, SignalDescription description, CancellationToken cancellationToken = default);
    Task SendCandidateAsync(string roomCode, SignalCandidate candidate, CancellationToken cancellationToken = default);
}
