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
    public const int Version = 2;

    public const byte RecordChunk = 0x01;
    public const byte RecordTrailer = 0x02;

    public const byte ContentTypeFile = 0x00;
    public const byte ContentTypeDirectory = 0x01;

    public const int Sha256Length = 32;

    public static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("ZPP2");

    /// <summary>Máximo razoável para evitar "zip bombs" ao descomprimir.</summary>
    public const int MaxChunkSize = 512 * 1024 * 1024;

    private const int TrailerSize = 1 + 8 + 8 + Sha256Length; // type + chunkCount + total + hash

    /// <summary>
    /// Lê o tamanho original (não comprimido) gravado no trailer, sem consumir o stream.
    /// Retorna -1 se não for possível (stream não-seekable ou formato inválido).
    /// </summary>
    public static long PeekOriginalSize(Stream input)
    {
        if (!input.CanSeek)
            return -1;

        var position = input.Position;
        try
        {
            if (input.Length < TrailerSize)
                return -1;

            input.Seek(-TrailerSize, SeekOrigin.End);

            Span<byte> trailer = stackalloc byte[1 + 8 + 8];
            input.ReadExactly(trailer);

            if (trailer[0] != RecordTrailer)
                return -1;

            return BitConverter.ToInt64(trailer.Slice(1 + 8, 8));
        }
        catch
        {
            return -1;
        }
        finally
        {
            input.Position = position;
        }
    }
}
