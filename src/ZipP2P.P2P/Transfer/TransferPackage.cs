using ZipP2P.Core.Compression;

namespace ZipP2P.P2P.Transfer;

/// <summary>Cria (comprime) e extrai os pacotes ".zpp2" trafegados numa transferência.</summary>
public static class TransferPackage
{
    /// <summary>Comprime o arquivo ou pasta <paramref name="sourcePath"/> em <paramref name="packagePath"/>.</summary>
    /// <returns>O tipo de conteúdo (<see cref="Zpp2Format.ContentTypeFile"/> ou <see cref="Zpp2Format.ContentTypeDirectory"/>).</returns>
    public static async Task<byte> CreateAsync(
        string sourcePath,
        string packagePath,
        CompressionOptions? options = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var isFolder = Directory.Exists(sourcePath);
        if (!isFolder && !File.Exists(sourcePath))
            throw new FileNotFoundException("The file or folder to send was not found.", sourcePath);

        try
        {
            await using var output = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            if (isFolder)
            {
                await FolderCodec.CompressAsync(sourcePath, output, options, progress, cancellationToken).ConfigureAwait(false);
                return Zpp2Format.ContentTypeDirectory;
            }

            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await ChunkedCompressor.CompressAsync(input, output, options, progress, cancellationToken).ConfigureAwait(false);
            return Zpp2Format.ContentTypeFile;
        }
        catch
        {
            TryDelete(packagePath);
            throw;
        }
    }

    /// <summary>
    /// Extrai o pacote recebido para dentro de <paramref name="destinationDir"/>, sem sobrescrever nada:
    /// se já existir um item com o mesmo nome, usa "nome (1)", "nome (2)"...
    /// </summary>
    /// <returns>Caminho final do arquivo ou pasta extraído.</returns>
    public static async Task<string> ExtractAsync(
        string packagePath,
        string fileName,
        byte contentType,
        string destinationDir,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDir);
        var isFolder = contentType == Zpp2Format.ContentTypeDirectory;
        var target = GetUniquePath(destinationDir, SanitizeFileName(fileName), isFolder);

        try
        {
            await using var input = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (isFolder)
            {
                await FolderCodec.ExtractAsync(input, target, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await ChunkedDecompressor.DecompressAsync(input, output, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (isFolder)
            {
                try
                {
                    Directory.Delete(target, recursive: true);
                }
                catch
                {
                    // ignora falha de limpeza
                }
            }
            else
            {
                TryDelete(target);
            }

            throw;
        }

        return target;
    }

    /// <summary>Reduz um nome vindo do par a um nome de arquivo simples (sem diretórios nem caracteres inválidos).</summary>
    public static string SanitizeFileName(string name)
    {
        var candidate = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/').Split('/')[^1]);
        foreach (var c in Path.GetInvalidFileNameChars())
            candidate = candidate.Replace(c, '_');

        candidate = candidate.Trim().TrimEnd('.');
        return candidate is "" or "." or ".." ? "received" : candidate;
    }

    /// <summary>Retorna um caminho dentro de <paramref name="directory"/> que ainda não existe.</summary>
    public static string GetUniquePath(string directory, string name, bool isDirectory)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignora falha de limpeza
        }
    }
}
