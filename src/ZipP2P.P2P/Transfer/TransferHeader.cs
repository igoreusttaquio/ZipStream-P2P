namespace ZipP2P.P2P.Transfer;

/// <summary>
/// Metadados enviados no início de uma transferência. O <paramref name="TransferId"/> identifica o
/// pacote de forma estável, permitindo ao destinatário retomar um download parcial (inclusive após
/// fechar e reabrir o app).
/// </summary>
/// <param name="Size">Tamanho do pacote, ou -1 se o remetente ainda está compactando (o tamanho final chega no fim).</param>
/// <param name="EstimatedSize">Estimativa do tamanho final enquanto <paramref name="Size"/> é -1 (para o progresso).</param>
public sealed record TransferHeader(Guid TransferId, string FileName, long Size, byte ContentType, long EstimatedSize = 0);
