using ZipP2P.Core.Compression;

namespace ZipP2P.Core.Tests;

public class FolderCodecTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "zpp2-tests-" + Guid.NewGuid().ToString("N"));

    public FolderCodecTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // ignora falhas de limpeza
        }
    }

    private string CreateSampleTree()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub", "nested"));
        Directory.CreateDirectory(Path.Combine(src, "empty"));

        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), new byte[4096]);
        File.WriteAllText(Path.Combine(src, "sub", "nested", "c.txt"), "nested content");
        File.WriteAllBytes(Path.Combine(src, "empty-file.txt"), Array.Empty<byte>()); // arquivo vazio (0 bytes)
        return src;
    }

    [Fact]
    public async Task RoundTrip_Folder_PreservesTree()
    {
        var src = CreateSampleTree();

        using var compressed = new MemoryStream();
        await FolderCodec.CompressAsync(src, compressed);

        compressed.Position = 0;
        var dest = Path.Combine(_root, "dest");
        await FolderCodec.ExtractAsync(compressed, dest);

        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "b.bin")));
        Assert.Equal(4096, new FileInfo(Path.Combine(dest, "sub", "b.bin")).Length);
        Assert.Equal("nested content", File.ReadAllText(Path.Combine(dest, "sub", "nested", "c.txt")));
        Assert.True(Directory.Exists(Path.Combine(dest, "empty")));
        Assert.True(File.Exists(Path.Combine(dest, "empty-file.txt")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(dest, "empty-file.txt")));
    }

    [Fact]
    public async Task RoundTrip_Folder_LongAndUnicodeNames()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);

        var longName = new string('x', 120) + ".txt"; // >100 caracteres força cabeçalho PAX
        var unicodeName = "arquivo-com-acentuação-ñ.txt";

        File.WriteAllText(Path.Combine(src, longName), "longo");
        File.WriteAllText(Path.Combine(src, unicodeName), "unicode");

        using var compressed = new MemoryStream();
        await FolderCodec.CompressAsync(src, compressed);

        compressed.Position = 0;
        var dest = Path.Combine(_root, "dest");
        await FolderCodec.ExtractAsync(compressed, dest);

        Assert.Equal("longo", File.ReadAllText(Path.Combine(dest, longName)));
        Assert.Equal("unicode", File.ReadAllText(Path.Combine(dest, unicodeName)));
    }

    [Fact]
    public async Task Folder_ContentType_IsDirectory()
    {
        var src = CreateSampleTree();

        using var compressed = new MemoryStream();
        await FolderCodec.CompressAsync(src, compressed);

        compressed.Position = 0;
        Assert.Equal(Zpp2Format.ContentTypeDirectory, FolderCodec.PeekContentType(compressed));
    }

    [Fact]
    public async Task File_ContentType_IsFile()
    {
        var original = new byte[1024];
        new Random(7).NextBytes(original);

        using var source = new MemoryStream(original);
        using var compressed = new MemoryStream();
        await ChunkedCompressor.CompressAsync(source, compressed);

        compressed.Position = 0;
        Assert.Equal(Zpp2Format.ContentTypeFile, FolderCodec.PeekContentType(compressed));
    }

    [Fact]
    public async Task Extract_RejectsPathTraversal()
    {
        // Constrói um tar malicioso diretamente (com um nome de entrada que tenta sair do destino).
        using var tarStream = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(tarStream, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new System.Formats.Tar.PaxTarEntry(
                System.Formats.Tar.TarEntryType.RegularFile, "../evil.txt");
            var data = System.Text.Encoding.UTF8.GetBytes("boom");
            entry.DataStream = new MemoryStream(data);
            writer.WriteEntry(entry);
        }

        tarStream.Position = 0;

        // Serializa o tar como um ".zpp2" de pasta e tenta extrair.
        using var compressed = new MemoryStream();
        await ChunkedCompressor.CompressAsync(
            tarStream, compressed, contentType: Zpp2Format.ContentTypeDirectory);

        compressed.Position = 0;
        var dest = Path.Combine(_root, "dest");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => FolderCodec.ExtractAsync(compressed, dest));
    }
}
