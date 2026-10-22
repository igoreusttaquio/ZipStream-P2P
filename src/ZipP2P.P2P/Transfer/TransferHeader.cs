namespace ZipP2P.P2P.Transfer;

/// <summary>
/// Metadados enviados no início de uma transferência. O <paramref name="TransferId"/> identifica o
/// pacote de forma estável, permitindo ao destinatário retomar um download parcial (inclusive após
/// fechar e reabrir o app).
/// </summary>
public sealed record TransferHeader(Guid TransferId, string FileName, long Size, byte ContentType);
