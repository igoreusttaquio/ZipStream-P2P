using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipP2P.App.Updates;

/// <summary>Arquivo de uma release. <paramref name="Sha256"/> vem do campo <c>digest</c> da API (hex).</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>Versão mais nova publicada. <paramref name="Installer"/> só existe onde o app sabe se instalar (Windows).</summary>
public sealed record AvailableUpdate(Version Version, string Tag, Uri ReleasePage, ReleaseAsset? Installer);

/// <summary>
/// Procura versões novas nas releases do GitHub e baixa o instalador, conferindo o SHA-256 publicado
/// pelo GitHub antes de entregá-lo para execução.
/// </summary>
public sealed class UpdateChecker
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/igoreusttaquio/ZipStream-P2P/releases/latest";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly string _runtimeId;

    /// <param name="currentVersion">Versão deste app (ex.: <c>1.2.0</c> ou <c>1.2.0+abc123</c>).</param>
    /// <param name="runtimeId">Plataforma, como nos nomes dos arquivos da release (ex.: <c>win-x64</c>).</param>
    public UpdateChecker(HttpClient http, string currentVersion, string runtimeId)
    {
        _http = http;
        _currentVersion = currentVersion;
        _runtimeId = runtimeId;
    }

    public static UpdateChecker ForCurrentApp() => new(SharedHttp, Logging.AppLog.AppVersion, CurrentRuntimeId);

    public static string CurrentRuntimeId
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            return $"{os}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        }
    }

    /// <summary>Versões de desenvolvimento (ex.: <c>0.0.0-dev</c>) não oferecem atualização.</summary>
    public bool IsDevelopmentBuild => !TryParseVersion(_currentVersion, out var version) || version == new Version(0, 0, 0);

    /// <summary>Versão para exibir, sem os metadados de build (ex.: <c>1.2.0</c> ou <c>0.0.0-dev</c>).</summary>
    public string CurrentVersion => TryParseVersion(_currentVersion, out var version)
        ? version.ToString(3)
        : _currentVersion.Split('+')[0];

    /// <summary>Retorna a versão publicada se for mais nova que esta; senão, null.</summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsDevelopmentBuild)
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ZipStream", CurrentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Empty release information.");

        if (release.Draft || release.Prerelease || !TryParseVersion(release.TagName, out var latest))
            return null;

        TryParseVersion(_currentVersion, out var current);
        if (latest <= current)
            return null;

        return new AvailableUpdate(latest, release.TagName, new Uri(release.HtmlUrl), FindInstaller(release));
    }

    /// <summary>
    /// Baixa o arquivo para <paramref name="directory"/> e confere tamanho e SHA-256. Se não conferir, apaga o
    /// arquivo e lança <see cref="InvalidDataException"/>: um instalador adulterado nunca é entregue.
    /// </summary>
    public async Task<string> DownloadAsync(
        ReleaseAsset asset,
        string directory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (asset.Sha256 is null)
            throw new InvalidDataException($"The release does not publish a checksum for {asset.Name}; download it from the release page.");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(asset.Name));
        var partial = path + ".part";

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    if (asset.Size > 0)
                        progress?.Report(Math.Min(1.0, (double)total / asset.Size));
                }

                if (asset.Size > 0 && total != asset.Size)
                    throw new InvalidDataException($"Downloaded {total} bytes of {asset.Name}, expected {asset.Size}.");
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The checksum of {asset.Name} does not match the release; the download was discarded.");

            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    /// <summary>Lê <c>1.2.3</c>, <c>v1.2.3</c> ou <c>1.2.3+metadados</c>. Pré-versões (<c>-dev</c>, <c>-rc1</c>) não contam.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var core = text.Trim().TrimStart('v', 'V');
        var plus = core.IndexOf('+');
        if (plus >= 0)
            core = core[..plus];
        if (core.Contains('-'))
            return false;

        if (!Version.TryParse(core, out var parsed))
            return false;

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private ReleaseAsset? FindInstaller(GitHubRelease release)
    {
        // Só o Windows tem instalador que o app sabe executar; Linux/macOS abrem a página da release.
        if (!_runtimeId.StartsWith("win-", StringComparison.Ordinal))
            return null;

        var asset = release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("ZipStream-Setup-", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith($"-{_runtimeId}.exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return null;

        var sha256 = asset.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..]
            : null;
        return new ReleaseAsset(asset.Name, new Uri(asset.BrowserDownloadUrl), asset.Size, sha256);
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);
}
