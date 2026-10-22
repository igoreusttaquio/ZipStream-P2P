namespace ZipP2P.P2P;

/// <summary>
/// Orquestra o estabelecimento de uma conexão P2P: amarra o <see cref="ITransport"/>
/// ao <see cref="ISignalingClient"/> e executa o fluxo offer/answer.
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly ISignalingClient _signaling;
    private readonly string _roomCode;
    private readonly string _label;

    public PeerConnection(ITransport transport, ISignalingClient signaling, string roomCode, string label = "data")
    {
        _transport = transport;
        _signaling = signaling;
        _roomCode = roomCode;
        _label = label;
    }

    public ITransport Transport => _transport;

    /// <summary>
    /// Conecta ao servidor de sinalização, entra na sala e estabelece a conexão.
    /// O lado <paramref name="asOfferer"/> cria o canal de dados (após o par estar na sala).
    /// </summary>
    public async Task ConnectAsync(bool asOfferer, CancellationToken cancellationToken = default)
    {
        // Transporte → sinalização (envia SDP/candidatos locais ao servidor).
        _transport.LocalDescriptionReady += d => _ = _signaling.SendDescriptionAsync(_roomCode, d);
        _transport.LocalCandidateReady += c => _ = _signaling.SendCandidateAsync(_roomCode, c);

        // Sinalização → transporte (recebe SDP/candidatos do par).
        _signaling.DescriptionReceived += d => _transport.OnRemoteDescription(d);
        _signaling.CandidateReceived += c => _transport.OnRemoteCandidate(c);

        // Assina antes de entrar na sala: o servidor avisa "PeerJoined" durante o JoinRoom
        // quando o par já está lá (caso comum numa reconexão).
        var peerJoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _signaling.PeerJoined += () => peerJoined.TrySetResult();

        await _signaling.StartAsync(cancellationToken).ConfigureAwait(false);
        await _signaling.JoinRoomAsync(_roomCode, cancellationToken).ConfigureAwait(false);

        if (asOfferer)
        {
            await peerJoined.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _transport.CreateDataChannel(_label);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _signaling.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
