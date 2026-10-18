using ZipP2P.Contracts;

namespace ZipP2P.P2P;

/// <summary>
/// Transporte de dados P2P. Abstrai o backend (ex.: libdatachannel) para que a troca de
/// implementação seja localizada. Cobre o plano de dados (chunks) e a sinalização
/// (troca de SDP e candidatos ICE) necessária para estabelecer a conexão.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Disparado quando uma mensagem binária (chunk) é recebida.</summary>
    event Action<ReadOnlyMemory<byte>>? MessageReceived;

    /// <summary>Disparado quando o canal de dados é estabelecido (dados podem fluir).</summary>
    event Action? Connected;

    /// <summary>Disparado quando a conexão é encerrada ou falha.</summary>
    event Action? Disconnected;

    /// <summary>Descrição SDP local gerada — deve ser encaminhada ao par via sinalização.</summary>
    event Action<SignalDescription>? LocalDescriptionReady;

    /// <summary>Candidato ICE local gerado — deve ser encaminhado ao par via sinalização.</summary>
    event Action<SignalCandidate>? LocalCandidateReady;

    bool IsConnected { get; }

    /// <summary>Cria o canal de dados (chamado pelo lado que inicia a conexão).</summary>
    void CreateDataChannel(string label);

    /// <summary>Recebe a descrição SDP do par (via sinalização).</summary>
    void OnRemoteDescription(SignalDescription description);

    /// <summary>Recebe um candidato ICE do par (via sinalização).</summary>
    void OnRemoteCandidate(SignalCandidate candidate);

    /// <summary>Envia dados binários pelo canal.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
