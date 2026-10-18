namespace ZipP2P.P2P;

/// <summary>
/// Transporte de dados P2P (plano de dados). Abstrai o backend (ex.: libdatachannel)
/// para que a troca de implementação seja localizada, sem afetar compressão/E2EE/protocolo.
/// O estabelecimento da conexão (sinalização de SDP/candidatos) fica fora desta interface.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Disparado quando uma mensagem binária (chunk) é recebida.</summary>
    event Action<ReadOnlyMemory<byte>>? MessageReceived;

    /// <summary>Disparado quando o canal de dados é estabelecido.</summary>
    event Action? Connected;

    bool IsConnected { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
