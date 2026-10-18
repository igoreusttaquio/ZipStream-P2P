using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace ZipP2P.Core.Compression;

/// <summary>
/// Compacta um stream em chunks independentes (zstd) com hash SHA-256 por chunk,
/// escrevendo no contêiner ".zpp2". Nunca carrega o arquivo inteiro em memória.
/// </summary>
public static class ChunkedCompressor
{
    public static async Task CompressAsync(
        Stream input,
        Stream output,
        CompressionOptions? options = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        byte contentType = Zpp2Format.ContentTypeFile)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        options ??= CompressionOptions.Default;

        if (options.ChunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "ChunkSize must be positive.");
        if (options.Level is < 1 or > 22)
            throw new ArgumentOutOfRangeException(nameof(options), "Level must be between 1 and 22.");

        long totalBytes = TryGetLength(input);

        var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        writer.Write(Zpp2Format.Magic);
        writer.Write(Zpp2Format.Version);
        writer.Write(options.ChunkSize);
        writer.Write((byte)options.Level);
        writer.Write(contentType);

        var chunkBuffer = ArrayPool<byte>.Shared.Rent(options.ChunkSize);
        var compressor = new ZstdSharp.Compressor(options.Level);
        using var globalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long bytesProcessed = 0;
        long chunksProcessed = 0;

        try
        {
            while (true)
            {
                int read = await ReadChunkAsync(input, chunkBuffer, options.ChunkSize, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                var original = chunkBuffer.AsSpan(0, read);

                globalHash.AppendData(original);
                byte[] chunkHash = SHA256.HashData(original);
                byte[] compressed = compressor.Wrap(original).ToArray();

                writer.Write(Zpp2Format.RecordChunk);
                writer.Write(read);
                writer.Write(compressed.Length);
                writer.Write(chunkHash);
                writer.Write(compressed);

                bytesProcessed += read;
                chunksProcessed++;
                progress?.Report(new CompressionProgress(
                    CompressionPhase.Compressing, bytesProcessed, totalBytes, chunksProcessed));
            }

            writer.Write(Zpp2Format.RecordTrailer);
            writer.Write(chunksProcessed);
            writer.Write(bytesProcessed);
            writer.Write(globalHash.GetHashAndReset());

            writer.Flush();
        }
        finally
        {
            compressor.Dispose();
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }
    }

    private static async Task<int> ReadChunkAsync(
        Stream input, byte[] buffer, int maxBytes, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < maxBytes)
        {
            int read = await input
                .ReadAsync(buffer.AsMemory(total, maxBytes - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        return total;
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
