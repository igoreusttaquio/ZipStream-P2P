using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.P2P;

/// <summary>Uma conexão P2P já aberta (canal de dados pronto). Descartar encerra a conexão.</summary>
public interface IPeerLink : IAsyncDisposable
{
    ITransport Transport { get; }
}

/// <summary>Cria conexões P2P para uma sala. Abstraído para permitir testes da reconexão.</summary>
public interface IPeerLinkFactory
{
    /// <summary>
    /// Entra na sala e aguarda o canal de dados abrir. Espera o par entrar na sala sem limite
    /// de tempo (até <paramref name="cancellationToken"/>).
    /// </summary>
    Task<IPeerLink> ConnectAsync(string roomCode, bool asOfferer, CancellationToken cancellationToken);
}

/// <summary>Fábrica real: sinalização SignalR + transporte libdatachannel.</summary>
public sealed class SignalingPeerLinkFactory : IPeerLinkFactory
{
    private readonly string _signalingUrl;
    private readonly IReadOnlyList<string> _iceServers;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _openTimeout;

    public SignalingPeerLinkFactory(
        string signalingUrl,
        IReadOnlyList<string>? iceServers = null,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? openTimeout = null)
    {
        _signalingUrl = signalingUrl;
        _iceServers = iceServers ?? Array.Empty<string>();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _openTimeout = openTimeout ?? TimeSpan.FromSeconds(45);
    }

    /// <summary>
    /// Verifica se o servidor de sinalização responde (lança <see cref="SignalingException"/> se não).
    /// Útil antes de etapas demoradas, como comprimir um arquivo grande.
    /// </summary>
    public async Task CheckServerAsync(CancellationToken cancellationToken)
    {
        await using var signaling = new SignalRSignalingClient(_signalingUrl, _loggerFactory.CreateLogger("Signaling"));
        await signaling.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IPeerLink> ConnectAsync(string roomCode, bool asOfferer, CancellationToken cancellationToken)
    {
        var transport = new LibDataChannelTransport(_iceServers, _loggerFactory.CreateLogger("WebRTC"));
        var signaling = new SignalRSignalingClient(_signalingUrl, _loggerFactory.CreateLogger("Signaling"));
        var peer = new PeerConnection(transport, signaling, roomCode);

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Connected += () => opened.TrySetResult();
        transport.Disconnected += () => opened.TrySetException(
            new ConnectionLostException("The peer connection failed while being established (ICE/DTLS)."));

        try
        {
            await peer.ConnectAsync(asOfferer, cancellationToken).ConfigureAwait(false);

            // O destinatário pode entrar antes do remetente: espera a oferta sem limite.
            // O remetente já sabe que o par está na sala, então a negociação tem prazo.
            if (asOfferer)
            {
                try
                {
                    await opened.Task.WaitAsync(_openTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new ConnectionLostException(
                        $"Could not open a direct connection within {_openTimeout.TotalSeconds:F0}s. " +
                        "Both devices may be behind restrictive NATs/firewalls; a TURN server may be required.");
                }
            }
            else
            {
                await opened.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new Link(peer);
        }
        catch
        {
            await peer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Link(PeerConnection peer) : IPeerLink
    {
        public ITransport Transport => peer.Transport;

        public ValueTask DisposeAsync() => peer.DisposeAsync();
    }
}
