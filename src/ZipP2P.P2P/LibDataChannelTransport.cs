using DataChannelDotnet;
using DataChannelDotnet.Bindings;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZipP2P.Contracts;

namespace ZipP2P.P2P;

/// <summary>
/// Implementação de <see cref="ITransport"/> usando libdatachannel (via DataChannelDotnet).
/// </summary>
public sealed class LibDataChannelTransport : ITransport
{
    private readonly IRtcPeerConnection _peer;
    private readonly ILogger _logger;
    private readonly object _receiveLock = new();
    private readonly Queue<ReadOnlyMemory<byte>> _pending = new();
    private Action<ReadOnlyMemory<byte>>? _messageReceived;
    private IRtcDataChannel? _channel;
    private int _disposed;

    public LibDataChannelTransport(IEnumerable<string>? iceServers = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;

        var configuration = new RtcPeerConfiguration
        {
            IceServers = (iceServers ?? Array.Empty<string>()).ToList(),
        };

        _logger.LogDebug("Creating peer connection (ICE servers: {Count})", configuration.IceServers.Count);
        _peer = new RtcPeerConnection(configuration);

        _peer.OnLocalDescriptionSafe += (_, desc) =>
            LocalDescriptionReady?.Invoke(new SignalDescription(desc.Sdp, ToSignalType(desc.Type)));

        _peer.OnCandidateSafe += (_, cand) =>
        {
            _logger.LogDebug("Local ICE candidate: {Candidate}", cand.Content);
            LocalCandidateReady?.Invoke(new SignalCandidate(cand.Content, cand.Mid));
        };

        _peer.OnDataChannel += (_, channel) => AttachChannel(channel);

        _peer.OnIceStateChange += (_, state) => _logger.LogDebug("ICE state: {State}", state);
        _peer.OnGatheringStateChange += (_, state) => _logger.LogDebug("ICE gathering: {State}", state);

        _peer.OnConnectionStateChange += (peer, state) =>
        {
            _logger.LogInformation("Peer connection state: {State}", state);

            if (state == rtcState.RTC_CONNECTED)
                _logger.LogInformation("Connected: local {Local} <-> remote {Remote}", SafeAddress(() => peer.LocalAddress), SafeAddress(() => peer.RemoteAddress));

            if (state is rtcState.RTC_FAILED or rtcState.RTC_CLOSED)
                Disconnected?.Invoke();
        };
    }

    /// <summary>
    /// Mensagens que chegam antes de haver assinante (o par pode falar logo que o canal abre)
    /// ficam guardadas e são entregues ao primeiro assinante.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? MessageReceived
    {
        add
        {
            lock (_receiveLock)
            {
                _messageReceived += value;
                while (value is not null && _pending.TryDequeue(out var message))
                    value(message);
            }
        }
        remove
        {
            lock (_receiveLock)
                _messageReceived -= value;
        }
    }

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<SignalDescription>? LocalDescriptionReady;
    public event Action<SignalCandidate>? LocalCandidateReady;

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && (_channel?.IsOpen ?? false);

    public void CreateDataChannel(string label)
    {
        var channel = _peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = label });
        AttachChannel(channel);
    }

    public void OnRemoteDescription(SignalDescription description)
    {
        _logger.LogDebug("Remote description received ({Type})", description.Type);
        _peer.SetRemoteDescription(new RtcDescription
        {
            Sdp = description.Sdp,
            Type = ToRtcType(description.Type),
        });
    }

    public void OnRemoteCandidate(SignalCandidate candidate)
    {
        _logger.LogDebug("Remote ICE candidate: {Candidate}", candidate.Content);
        _peer.AddRemoteCandidate(new RtcCandidate
        {
            Content = candidate.Content,
            Mid = candidate.Mid,
        });
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Transport is not connected.");

        _channel!.Send(data.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _logger.LogDebug("Closing peer connection");
            _peer.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void AttachChannel(IRtcDataChannel channel)
    {
        _channel = channel;
        channel.OnOpen += _ =>
        {
            _logger.LogInformation("Data channel '{Label}' open", channel.Label);
            Connected?.Invoke();
        };
        channel.OnClose += _ =>
        {
            _logger.LogInformation("Data channel '{Label}' closed", channel.Label);
            Disconnected?.Invoke();
        };
        channel.OnError += (_, error) => _logger.LogError("Data channel error: {Error}", error);
        channel.OnBinaryReceivedSafe += (_, data) => Deliver(data.Data.ToArray());
    }

    private void Deliver(ReadOnlyMemory<byte> message)
    {
        Action<ReadOnlyMemory<byte>>? handler;
        lock (_receiveLock)
        {
            handler = _messageReceived;
            if (handler is null)
            {
                _pending.Enqueue(message);
                return;
            }
        }

        handler(message);
    }

    private static string SafeAddress(Func<string> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return "?";
        }
    }

    private static SignalDescriptionType ToSignalType(RtcDescriptionType type) =>
        type == RtcDescriptionType.Answer ? SignalDescriptionType.Answer : SignalDescriptionType.Offer;

    private static RtcDescriptionType ToRtcType(SignalDescriptionType type) =>
        type == SignalDescriptionType.Answer ? RtcDescriptionType.Answer : RtcDescriptionType.Offer;
}
