using DataChannelDotnet;
using DataChannelDotnet.Bindings;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;

namespace ZipP2P.P2P;

/// <summary>
/// Implementação de <see cref="ITransport"/> usando libdatachannel (via DataChannelDotnet).
/// </summary>
public sealed class LibDataChannelTransport : ITransport
{
    private readonly IRtcPeerConnection _peer;
    private IRtcDataChannel? _channel;

    public LibDataChannelTransport(RtcPeerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _peer = new RtcPeerConnection(configuration);

        _peer.OnLocalDescriptionSafe += (_, desc) =>
            LocalDescriptionReady?.Invoke(new SignalDescription(desc.Sdp, ToSignalType(desc.Type)));

        _peer.OnCandidateSafe += (_, cand) =>
            LocalCandidateReady?.Invoke(new SignalCandidate(cand.Content, cand.Mid));

        _peer.OnDataChannel += (_, channel) => AttachChannel(channel);

        _peer.OnConnectionStateChange += (_, state) =>
        {
            if (state is rtcState.RTC_FAILED or rtcState.RTC_CLOSED)
                Disconnected?.Invoke();
        };
    }

    public event Action<ReadOnlyMemory<byte>>? MessageReceived;
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<SignalDescription>? LocalDescriptionReady;
    public event Action<SignalCandidate>? LocalCandidateReady;

    public bool IsConnected => _channel?.IsOpen ?? false;

    public void CreateDataChannel(string label)
    {
        var channel = _peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = label });
        AttachChannel(channel);
    }

    public void OnRemoteDescription(SignalDescription description)
    {
        _peer.SetRemoteDescription(new RtcDescription
        {
            Sdp = description.Sdp,
            Type = ToRtcType(description.Type),
        });
    }

    public void OnRemoteCandidate(SignalCandidate candidate)
    {
        _peer.AddRemoteCandidate(new RtcCandidate
        {
            Content = candidate.Content,
            Mid = candidate.Mid,
        });
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_channel is not { IsOpen: true })
            throw new InvalidOperationException("Transport is not connected.");

        _channel.Send(data.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _peer.Dispose();
        return ValueTask.CompletedTask;
    }

    private void AttachChannel(IRtcDataChannel channel)
    {
        _channel = channel;
        channel.OnOpen += _ => Connected?.Invoke();
        channel.OnClose += _ => Disconnected?.Invoke();
        channel.OnBinaryReceivedSafe += (_, data) => MessageReceived?.Invoke(data.Data.ToArray());
    }

    private static SignalDescriptionType ToSignalType(RtcDescriptionType type) =>
        type == RtcDescriptionType.Answer ? SignalDescriptionType.Answer : SignalDescriptionType.Offer;

    private static RtcDescriptionType ToRtcType(SignalDescriptionType type) =>
        type == SignalDescriptionType.Answer ? RtcDescriptionType.Answer : RtcDescriptionType.Offer;
}
