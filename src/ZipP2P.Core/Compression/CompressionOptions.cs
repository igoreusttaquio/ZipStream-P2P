namespace ZipP2P.Core.Compression;

/// <summary>Configuração do motor de compressão em chunks.</summary>
public sealed record CompressionOptions
{
    /// <summary>Tamanho do chunk (não comprimido) em bytes. Padrão: 4 MB.</summary>
    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>Nível de compressão zstd (1..22). Padrão: 3 (equilibrado).</summary>
    public int Level { get; init; } = 3;

    public static CompressionOptions Default => new();
}
