using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZipP2P.Contracts;

namespace ZipP2P.P2P;

/// <summary>Cliente de sinalização baseado em SignalR.</summary>
public sealed class SignalRSignalingClient : ISignalingClient
{
    private readonly HubConnection _connection;
    private readonly ILogger _logger;
    private readonly string _serverUrl;

    public SignalRSignalingClient(string serverUrl, ILogger? logger = null)
    {
        _serverUrl = serverUrl;
        _logger = logger ?? NullLogger.Instance;
        _connection = new HubConnectionBuilder()
            .WithUrl(serverUrl)
            .Build();

        _connection.On("PeerJoined", () =>
        {
            _logger.LogInformation("The other device joined the room");
            PeerJoined?.Invoke();
        });
        _connection.On<SignalDescription>("ReceiveDescription", d => DescriptionReceived?.Invoke(d));
        _connection.On<SignalCandidate>("ReceiveCandidate", c => CandidateReceived?.Invoke(c));
        _connection.Closed += ex =>
        {
            if (ex is not null)
                _logger.LogWarning(ex, "Signaling connection closed with error");
            return Task.CompletedTask;
        };
    }

    public event Action? PeerJoined;
    public event Action<SignalDescription>? DescriptionReceived;
    public event Action<SignalCandidate>? CandidateReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Connecting to signaling server {Url}", _serverUrl);
        try
        {
            await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SignalingException($"Could not reach the signaling server at {_serverUrl}: {ex.Message}", ex);
        }
    }

    public async Task JoinRoomAsync(string roomCode, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Joining room {Room}", roomCode);
        try
        {
            await _connection.InvokeAsync("JoinRoom", roomCode, cancellationToken).ConfigureAwait(false);
        }
        catch (HubException ex)
        {
            throw new SignalingException(ex.Message, ex);
        }
    }

    public Task LeaveRoomAsync(string roomCode, CancellationToken cancellationToken = default)
        => _connection.InvokeAsync("LeaveRoom", roomCode, cancellationToken);

    public Task SendDescriptionAsync(string roomCode, SignalDescription description, CancellationToken cancellationToken = default)
        => SafeInvokeAsync("SendDescription", roomCode, description, cancellationToken);

    public Task SendCandidateAsync(string roomCode, SignalCandidate candidate, CancellationToken cancellationToken = default)
        => SafeInvokeAsync("SendCandidate", roomCode, candidate, cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    // Chamadas disparadas por eventos do transporte (fire-and-forget): falhas só são registradas.
    private async Task SafeInvokeAsync(string method, string roomCode, object payload, CancellationToken cancellationToken)
    {
        try
        {
            await _connection.InvokeAsync(method, roomCode, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signaling call {Method} failed", method);
        }
    }
}

/// <summary>Falha ao falar com o servidor de sinalização (servidor fora do ar, sala cheia...).</summary>
public sealed class SignalingException : Exception
{
    public SignalingException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
