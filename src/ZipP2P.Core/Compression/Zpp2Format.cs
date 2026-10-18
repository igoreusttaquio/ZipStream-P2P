using System.Text;

namespace ZipP2P.Core.Compression;

/// <summary>
/// Define o formato de contêiner ".zpp2" (stream de chunks comprimidos).
/// Layout:
///   Header (13 bytes): magic "ZPP2" | versão int32 | chunkSize int32 | nível byte
///   Registros (prefixados por 1 byte de tipo):
///     0x01 chunk : originalLength int32 | compressedLength int32 | sha256(32) | payload
///     0x02 trailer: chunkCount int64 | totalOriginalBytes int64 | globalSha256(32)
/// Toda a leitura/gravação é little-endian (BinaryReader/Writer).
/// </summary>
public static class Zpp2Format
{
    public const int Magic = 0x3250505A; // "ZPP2" little-endian
    public const int Version = 1;

    public const byte RecordChunk = 0x01;
    public const byte RecordTrailer = 0x02;

    public const int Sha256Length = 32;

    public static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("ZPP2");

    /// <summary>Máximo razoável para evitar "zip bombs" ao descomprimir.</summary>
    public const int MaxChunkSize = 512 * 1024 * 1024;
}
