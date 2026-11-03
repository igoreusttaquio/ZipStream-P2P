using System.Net;
using System.Security.Cryptography;
using System.Text;
using ZipP2P.App.Updates;

namespace ZipP2P.App.Tests;

public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly byte[] InstallerBytes = Encoding.ASCII.GetBytes("fake installer contents");
    private static readonly string InstallerSha = Convert.ToHexStringLower(SHA256.HashData(InstallerBytes));

    private readonly string _dir = Directory.CreateTempSubdirectory("zpp2-update-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task NewerRelease_IsOffered_WithTheWindowsInstaller()
    {
        var checker = new UpdateChecker(Http(Release("v1.3.0")), "1.2.0+8fecc2d", "win-x64");

        var update = await checker.CheckAsync();

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 3, 0), update.Version);
        Assert.Equal("https://github.com/igoreusttaquio/ZipStream-P2P/releases/tag/v1.3.0", update.ReleasePage.ToString());
        Assert.Equal("ZipStream-Setup-1.3.0-win-x64.exe", update.Installer!.Name);
        Assert.Equal(InstallerSha, update.Installer.Sha256);
        Assert.Equal(InstallerBytes.Length, update.Installer.Size);
    }

    [Theory]
    [InlineData("1.3.0")]
    [InlineData("1.4.0")]
    public async Task SameOrOlderRelease_IsNotOffered(string current)
    {
        var checker = new UpdateChecker(Http(Release("v1.3.0")), current, "win-x64");

        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task OnLinuxAndMac_TheUpdateHasNoInstaller()
    {
        var update = await new UpdateChecker(Http(Release("v1.3.0")), "1.2.0", "linux-x64").CheckAsync();

        Assert.NotNull(update);
        Assert.Null(update.Installer);
    }

    [Fact]
    public async Task DevelopmentBuild_DoesNotEvenAskGitHub()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("should not be called"));
        var checker = new UpdateChecker(new HttpClient(handler), "0.0.0-dev", "win-x64");

        Assert.True(checker.IsDevelopmentBuild);
        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task Prerelease_IsIgnored()
    {
        var checker = new UpdateChecker(Http(Release("v2.0.0", prerelease: true)), "1.2.0", "win-x64");

        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task GitHubError_Throws()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var checker = new UpdateChecker(new HttpClient(handler), "1.2.0", "win-x64");

        await Assert.ThrowsAsync<HttpRequestException>(() => checker.CheckAsync());
    }

    [Fact]
    public async Task Download_WithMatchingChecksum_ReturnsTheFile()
    {
        var checker = new UpdateChecker(Http(Release("v1.3.0")), "1.2.0", "win-x64");
        var update = await checker.CheckAsync();
        var progress = new List<double>();

        var path = await checker.DownloadAsync(update!.Installer!, _dir, new SyncProgress(progress.Add));

        Assert.Equal(InstallerBytes, File.ReadAllBytes(path));
        Assert.Equal(1.0, progress[^1]);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_WithWrongChecksum_IsDiscarded()
    {
        var checker = new UpdateChecker(Http(Release("v1.3.0"), installer: Encoding.ASCII.GetBytes("tampered contents!!!!!!")), "1.2.0", "win-x64");
        var update = await checker.CheckAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => checker.DownloadAsync(update!.Installer!, _dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_WithoutPublishedChecksum_IsRefused()
    {
        var checker = new UpdateChecker(Http(Release("v1.3.0", digest: false)), "1.2.0", "win-x64");
        var update = await checker.CheckAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => checker.DownloadAsync(update!.Installer!, _dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData("1.2.3", true, "1.2.3")]
    [InlineData("v1.2.3", true, "1.2.3")]
    [InlineData("1.2.3+abc123", true, "1.2.3")]
    [InlineData("1.2", true, "1.2.0")]
    [InlineData("0.0.0-dev", false, "0.0.0")]
    [InlineData("1.3.0-rc1", false, "0.0.0")]
    [InlineData("?", false, "0.0.0")]
    public void ParsesVersions(string text, bool ok, string expected)
    {
        Assert.Equal(ok, UpdateChecker.TryParseVersion(text, out var version));
        Assert.Equal(expected, version.ToString(3));
    }

    private static string Release(string tag, bool prerelease = false, bool digest = true)
    {
        var version = tag.TrimStart('v');
        var digestJson = digest ? $"\"digest\": \"sha256:{InstallerSha}\"," : string.Empty;
        return $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/igoreusttaquio/ZipStream-P2P/releases/tag/{{tag}}",
              "draft": false,
              "prerelease": {{(prerelease ? "true" : "false")}},
              "assets": [
                { "name": "ZipStream-linux-x64.tar.gz", "browser_download_url": "https://example.test/linux.tar.gz", "size": 10, "digest": "sha256:00" },
                { "name": "ZipStream-Setup-{{version}}-win-x64.exe", "browser_download_url": "https://example.test/setup.exe",
                  {{digestJson}} "size": {{InstallerBytes.Length}} }
              ]
            }
            """;
    }

    private static HttpClient Http(string releaseJson, byte[]? installer = null)
        => new(new FakeHandler(request => request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer ?? InstallerBytes) }));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
