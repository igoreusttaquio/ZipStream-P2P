using System.Security.Cryptography;
using System.Text;

namespace ZipP2P.Core.Compression;

/// <summary>
/// Descompacta um contêiner ".zpp2" em streaming, verificando o hash SHA-256 de cada
/// chunk e o hash global no trailer. Nunca carrega o arquivo inteiro em memória.
/// </summary>
public static class ChunkedDecompressor
{
    public static async Task<byte> DecompressAsync(
        Stream input,
        Stream output,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        int magic = reader.ReadInt32();
        if (magic != Zpp2Format.Magic)
            throw new InvalidDataException("Invalid format: magic does not match .zpp2.");

        int version = reader.ReadInt32();
        if (version != Zpp2Format.Version)
            throw new InvalidDataException($"Unsupported format version: {version}.");

        int chunkSize = reader.ReadInt32();
        if (chunkSize is <= 0 or > Zpp2Format.MaxChunkSize)
            throw new InvalidDataException($"Invalid chunk size: {chunkSize}.");

        byte level = reader.ReadByte();
        byte contentType = reader.ReadByte();

        long totalBytes = TryGetLength(input);

        using var decompressor = new ZstdSharp.Decompressor();
        using var globalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long bytesProcessed = 0;
        long chunksProcessed = 0;
        long expectedChunks = 0;
        long expectedTotal = 0;
        byte[]? expectedGlobalHash = null;

        while (true)
        {
            int recordType = reader.ReadByte();
            if (recordType == -1)
                throw new InvalidDataException("Truncated file: missing trailer record.");

            if (recordType == Zpp2Format.RecordTrailer)
            {
                expectedChunks = reader.ReadInt64();
                expectedTotal = reader.ReadInt64();
                expectedGlobalHash = reader.ReadBytes(Zpp2Format.Sha256Length);
                break;
            }

            if (recordType != Zpp2Format.RecordChunk)
                throw new InvalidDataException($"Unknown record type: 0x{recordType:X2}.");

            int originalLength = reader.ReadInt32();
            int compressedLength = reader.ReadInt32();

            if (originalLength <= 0 || originalLength > Zpp2Format.MaxChunkSize)
                throw new InvalidDataException($"Invalid original length: {originalLength}.");
            if (compressedLength <= 0 || compressedLength > Zpp2Format.MaxChunkSize)
                throw new InvalidDataException($"Invalid compressed length: {compressedLength}.");

            byte[] expectedChunkHash = reader.ReadBytes(Zpp2Format.Sha256Length);
            byte[] compressed = reader.ReadBytes(compressedLength);

            byte[] original;
            try
            {
                original = decompressor.Unwrap(compressed, originalLength).ToArray();
            }
            catch (ZstdSharp.ZstdException ex)
            {
                throw new InvalidDataException("Integrity check failed: corrupt chunk data.", ex);
            }
            if (original.Length != originalLength)
                throw new InvalidDataException(
                    $"Unexpected decompressed size: {original.Length} (expected {originalLength}).");

            byte[] actualChunkHash = SHA256.HashData(original);
            if (!CryptographicOperations.FixedTimeEquals(actualChunkHash, expectedChunkHash))
                throw new InvalidDataException("Integrity check failed: chunk hash mismatch.");

            globalHash.AppendData(original);
            await output.WriteAsync(original, cancellationToken).ConfigureAwait(false);

            bytesProcessed += original.Length;
            chunksProcessed++;
            progress?.Report(new CompressionProgress(
                CompressionPhase.Decompressing, bytesProcessed, totalBytes, chunksProcessed));
        }

        if (chunksProcessed != expectedChunks)
            throw new InvalidDataException(
                $"Chunk count mismatch: {chunksProcessed} (expected {expectedChunks}).");
        if (bytesProcessed != expectedTotal)
            throw new InvalidDataException(
                $"Total bytes mismatch: {bytesProcessed} (expected {expectedTotal}).");

        byte[] actualGlobalHash = globalHash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualGlobalHash, expectedGlobalHash!))
            throw new InvalidDataException("Integrity check failed: global hash mismatch.");

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        return contentType;
    }

    private static long TryGetLength(Stream input)
    {
        try
        {
            return input.CanSeek ? input.Length : -1;
        }
        catch (NotSupportedException)
        {
            return -1;
        }
    }
}
