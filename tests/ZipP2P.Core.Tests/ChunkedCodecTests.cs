using System.Security.Cryptography;
using ZipP2P.Core.Compression;

namespace ZipP2P.Core.Tests;

public class ChunkedCodecTests
{
    private static byte[] GenerateData(int length, int seed = 42)
    {
        var data = new byte[length];
        var rng = new Random(seed);
        rng.NextBytes(data);

        // Adiciona padrões repetitivos para exercitar a compressão.
        for (int i = 0; i < length; i += 128)
        {
            int run = Math.Min(64, length - i);
            Array.Fill(data, (byte)('A' + (i % 26)), i, run);
        }
        return data;
    }

    private static async Task<byte[]> RoundTripAsync(
        byte[] original, CompressionOptions? options = null)
    {
        using var source = new MemoryStream(original);
        using var compressed = new MemoryStream();
        await ChunkedCompressor.CompressAsync(source, compressed, options);

        compressed.Position = 0;
        using var result = new MemoryStream();
        await ChunkedDecompressor.DecompressAsync(compressed, result);
        return result.ToArray();
    }

    [Fact]
    public async Task RoundTrip_EmptyFile()
    {
        var result = await RoundTripAsync(Array.Empty<byte>());
        Assert.Empty(result);
    }

    [Fact]
    public async Task RoundTrip_SingleSmallChunk()
    {
        var original = GenerateData(1024);
        var result = await RoundTripAsync(original);
        Assert.Equal(original, result);
    }

    [Fact]
    public async Task RoundTrip_MultipleChunks()
    {
        var original = GenerateData(9 * 1024 * 1024 + 123); // ~2.25 chunks de 4MB
        var options = new CompressionOptions { ChunkSize = 4 * 1024 * 1024 };
        var result = await RoundTripAsync(original, options);
        Assert.Equal(original, result);
    }

    [Fact]
    public async Task RoundTrip_AllCompressionLevels()
    {
        var original = GenerateData(512 * 1024);
        foreach (int level in new[] { 1, 3, 10, 22 })
        {
            var options = new CompressionOptions { ChunkSize = 64 * 1024, Level = level };
            var result = await RoundTripAsync(original, options);
            Assert.Equal(original, result);
        }
    }

    [Fact]
    public async Task Compression_ActuallyReducesSize()
    {
        var original = new byte[4 * 1024 * 1024];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 7);

        using var source = new MemoryStream(original);
        using var compressed = new MemoryStream();
        await ChunkedCompressor.CompressAsync(source, compressed);

        Assert.True(compressed.Length < original.Length / 2);
    }

    [Fact]
    public async Task Decompress_DetectsCorruptedChunk()
    {
        var original = GenerateData(2 * 1024 * 1024);
        using var source = new MemoryStream(original);
        using var compressed = new MemoryStream();
        await ChunkedCompressor.CompressAsync(source, compressed, new CompressionOptions { ChunkSize = 1024 * 1024 });

        var bytes = compressed.ToArray();
        bytes[bytes.Length / 2] ^= 0xFF;

        using var corrupted = new MemoryStream(bytes);
        using var result = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(
            () => ChunkedDecompressor.DecompressAsync(corrupted, result));
    }

    [Fact]
    public async Task Compress_ReportsProgress()
    {
        var original = GenerateData(8 * 1024 * 1024);
        var reports = new List<CompressionProgress>();
        var progress = new Progress<CompressionProgress>(reports.Add);

        using var source = new MemoryStream(original);
        using var compressed = new MemoryStream();
        await ChunkedCompressor.CompressAsync(
            source, compressed, new CompressionOptions { ChunkSize = 1024 * 1024 }, progress);

        Assert.NotEmpty(reports);
        Assert.Equal(8, reports.Count);
        Assert.Equal(original.Length, reports[^1].BytesProcessed);
        Assert.Equal(original.Length, reports[^1].TotalBytes);
    }

    [Fact]
    public async Task RoundTrip_HashVerified()
    {
        var original = GenerateData(3 * 1024 * 1024 + 17);
        var result = await RoundTripAsync(original, new CompressionOptions { ChunkSize = 512 * 1024 });

        var expectedHash = SHA256.HashData(original);
        var actualHash = SHA256.HashData(result);
        Assert.Equal(expectedHash, actualHash);
    }
}
