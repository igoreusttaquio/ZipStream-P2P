using System.Threading.Channels;
using ZipP2P.Contracts;
using ZipP2P.P2P;

namespace ZipP2P.P2P.Tests;

/// <summary>Par de transportes em memória (entrega ordenada e assíncrona), com injeção de queda.</summary>
public sealed class MemoryTransport : ITransport
{
    private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private MemoryTransport _other = null!;
    private int _closed;
    private int _sent;

    private MemoryTransport()
    {
        _ = Task.Run(PumpAsync);
    }

    /// <summary>Derruba a conexão depois de este lado enviar esta quantidade de mensagens.</summary>
    public int? DropAfterMessages { get; set; }

    public int MessagesSent => Volatile.Read(ref _sent);

    public static (MemoryTransport A, MemoryTransport B) CreatePair()
    {
        var a = new MemoryTransport();
        var b = new MemoryTransport();
        a._other = b;
        b._other = a;
        return (a, b);
    }

    private readonly TaskCompletionSource _hasSubscriber = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<ReadOnlyMemory<byte>>? _messageReceived;

    public event Action<ReadOnlyMemory<byte>>? MessageReceived
    {
        add
        {
            _messageReceived += value;
            _hasSubscriber.TrySetResult();
        }
        remove => _messageReceived -= value;
    }

    public event Action? Connected;
    public event Action? Disconnected;
#pragma warning disable CS0067 // sem sinalização em memória
    public event Action<SignalDescription>? LocalDescriptionReady;
    public event Action<SignalCandidate>? LocalCandidateReady;
#pragma warning restore CS0067

    public bool IsConnected => Volatile.Read(ref _closed) == 0;

    public void CreateDataChannel(string label) => Connected?.Invoke();

    public void OnRemoteDescription(SignalDescription description)
    {
    }

    public void OnRemoteCandidate(SignalCandidate candidate)
    {
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Transport is not connected.");

        if (DropAfterMessages is { } limit && Interlocked.Increment(ref _sent) > limit)
        {
            Close();
            throw new InvalidOperationException("Simulated connection drop.");
        }

        if (DropAfterMessages is null)
            Interlocked.Increment(ref _sent);

        _other._inbound.Writer.TryWrite(data.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>Simula a queda da conexão (os dois lados percebem).</summary>
    public void Close()
    {
        CloseSide();
        _other.CloseSide();
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private void CloseSide()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _inbound.Writer.TryComplete();
            Disconnected?.Invoke();
        }
    }

    private async Task PumpAsync()
    {
        await _hasSubscriber.Task;
        await foreach (var message in _inbound.Reader.ReadAllAsync())
        {
            if (Volatile.Read(ref _closed) != 0)
                break;
            _messageReceived?.Invoke(message);
        }
    }
}

/// <summary>Fábrica de conexões em memória: pareia remetente e destinatário que pedem a mesma sala.</summary>
public sealed class MemoryPeerLinkFactory : IPeerLinkFactory
{
    private readonly object _lock = new();
    private TaskCompletionSource<MemoryTransport>? _waitingOfferer;
    private TaskCompletionSource<MemoryTransport>? _waitingAnswerer;

    /// <summary>Configura cada par recém-criado (remetente, destinatário) — ex.: injetar queda.</summary>
    public Action<int, MemoryTransport, MemoryTransport>? OnPair { get; set; }

    public int Pairs { get; private set; }

    public async Task<IPeerLink> ConnectAsync(string roomCode, bool asOfferer, CancellationToken cancellationToken)
    {
        Task<MemoryTransport> mine;
        lock (_lock)
        {
            var waiting = asOfferer ? _waitingAnswerer : _waitingOfferer;
            if (waiting is not null)
            {
                var (offerer, answerer) = MemoryTransport.CreatePair();
                OnPair?.Invoke(Pairs++, offerer, answerer);
                waiting.SetResult(asOfferer ? answerer : offerer);
                if (asOfferer)
                    _waitingAnswerer = null;
                else
                    _waitingOfferer = null;
                return new Link(asOfferer ? offerer : answerer);
            }

            var tcs = new TaskCompletionSource<MemoryTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (asOfferer)
                _waitingOfferer = tcs;
            else
                _waitingAnswerer = tcs;
            mine = tcs.Task;
        }

        return new Link(await mine.WaitAsync(cancellationToken));
    }

    private sealed class Link(MemoryTransport transport) : IPeerLink
    {
        public ITransport Transport => transport;

        public ValueTask DisposeAsync() => transport.DisposeAsync();
    }
}
