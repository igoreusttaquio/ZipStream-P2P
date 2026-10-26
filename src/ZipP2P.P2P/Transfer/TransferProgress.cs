namespace ZipP2P.P2P.Transfer;

/// <summary>Progresso da transferência pela rede (bytes do pacote comprimido).</summary>
/// <param name="BytesTransferred">Bytes já gravados pelo destinatário (o remetente conta pelas confirmações).</param>
/// <param name="TotalBytes">Tamanho total do pacote.</param>
/// <param name="BytesPerSecond">Vazão recente (ver <see cref="TransferRateMeter"/>); 0 enquanto não há medida.</param>
public sealed record TransferProgress(long BytesTransferred, long TotalBytes, double BytesPerSecond);
