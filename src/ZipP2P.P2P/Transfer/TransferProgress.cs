namespace ZipP2P.P2P.Transfer;

/// <summary>Progresso da transferência pela rede (bytes do pacote comprimido).</summary>
/// <param name="BytesTransferred">Bytes já confirmados (remetente) ou gravados (destinatário).</param>
/// <param name="TotalBytes">Tamanho total do pacote.</param>
/// <param name="BytesPerSecond">Vazão média desde o início desta conexão.</param>
public sealed record TransferProgress(long BytesTransferred, long TotalBytes, double BytesPerSecond);
