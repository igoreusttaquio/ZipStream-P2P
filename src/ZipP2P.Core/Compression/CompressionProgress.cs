namespace ZipP2P.Core.Compression;

public enum CompressionPhase
{
    Compressing,
    Decompressing
}

/// <summary>Snapshot de progresso reportado durante a operação em streaming.</summary>
/// <param name="Phase">Fase atual da operação.</param>
/// <param name="BytesProcessed">Bytes originais (não comprimidos) já processados.</param>
/// <param name="TotalBytes">Tamanho total original, ou -1 se desconhecido.</param>
/// <param name="ChunksProcessed">Quantidade de chunks já processados.</param>
public sealed record CompressionProgress(
    CompressionPhase Phase,
    long BytesProcessed,
    long TotalBytes,
    long ChunksProcessed);
