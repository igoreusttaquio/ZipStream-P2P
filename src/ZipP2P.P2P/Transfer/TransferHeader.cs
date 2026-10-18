namespace ZipP2P.P2P.Transfer;

/// <summary>Metadados enviados no início de uma transferência (primeira mensagem).</summary>
public sealed record TransferHeader(string FileName, long Size, byte ContentType);
