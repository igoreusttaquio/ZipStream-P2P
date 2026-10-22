using ZipP2P.Core.Compression;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.P2P.Tests;

public sealed class TransferPackageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-package-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\evil.dll", "evil.dll")]
    [InlineData("..", "received")]
    [InlineData("folder/", "folder")]
    public void SanitizeFileName_StripsDirectories(string input, string expected)
        => Assert.Equal(expected, TransferPackage.SanitizeFileName(input));

    [Fact]
    public async Task Extract_NeverOverwrites_ExistingItems()
    {
        var source = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(source, "new content");
        var package = Path.Combine(_dir, "p.zpp2");
        var type = await TransferPackage.CreateAsync(source, package);

        var dest = Path.Combine(_dir, "dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "notes.txt"), "existing");

        var first = await TransferPackage.ExtractAsync(package, "notes.txt", type, dest);
        var second = await TransferPackage.ExtractAsync(package, "notes.txt", type, dest);

        Assert.Equal(Path.Combine(dest, "notes (1).txt"), first);
        Assert.Equal(Path.Combine(dest, "notes (2).txt"), second);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(dest, "notes.txt")));
        Assert.Equal("new content", File.ReadAllText(first));
    }

    [Fact]
    public async Task Folder_IsExtracted_IntoItsOwnSubfolder()
    {
        var source = Path.Combine(_dir, "Photos");
        Directory.CreateDirectory(Path.Combine(source, "2024"));
        File.WriteAllText(Path.Combine(source, "2024", "a.jpg"), "jpg");
        var package = Path.Combine(_dir, "p.zpp2");
        var type = await TransferPackage.CreateAsync(source, package);

        var dest = Path.Combine(_dir, "Downloads");
        var extracted = await TransferPackage.ExtractAsync(package, "Photos", type, dest);

        Assert.Equal(Zpp2Format.ContentTypeDirectory, type);
        Assert.Equal(Path.Combine(dest, "Photos"), extracted);
        Assert.Equal("jpg", File.ReadAllText(Path.Combine(extracted, "2024", "a.jpg")));
    }

    [Fact]
    public async Task Create_MissingSource_Throws()
        => await Assert.ThrowsAsync<FileNotFoundException>(
            () => TransferPackage.CreateAsync(Path.Combine(_dir, "nope"), Path.Combine(_dir, "p.zpp2")));

    [Fact]
    public void Store_RoundTripsPendingTransfers()
    {
        var store = new TransferStore(_dir);
        var outgoing = new OutgoingTransfer(Guid.NewGuid(), "C:/x", "x", 0, 10, "ABC", DateTimeOffset.Now);
        var incoming = new IncomingTransfer(Guid.NewGuid(), "y", 1, 20, "D:/", "DEF", DateTimeOffset.Now);
        File.WriteAllBytes(store.PackagePath(outgoing.Id), new byte[10]);
        File.WriteAllBytes(store.PartialPath(incoming.Id), new byte[5]);
        store.Save(outgoing);
        store.Save(incoming);

        Assert.Equal(outgoing, Assert.Single(store.ListOutgoing()));
        Assert.Equal(incoming, store.FindIncoming(incoming.Id));
        Assert.Equal(5, store.BytesReceived(incoming));

        store.Delete(outgoing);
        store.Delete(incoming);
        Assert.Empty(store.ListOutgoing());
        Assert.Empty(store.ListIncoming());
        Assert.False(File.Exists(store.PartialPath(incoming.Id)));
    }
}
