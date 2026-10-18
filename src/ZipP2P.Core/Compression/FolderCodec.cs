using System.Formats.Tar;
using System.IO.Pipelines;
using System.Text;

namespace ZipP2P.Core.Compression;

/// <summary>
/// Compacta/extrai uma pasta inteira em streaming: serializa a árvore como tar
/// e a alimenta no <see cref="ChunkedCompressor"/>. Nada é carregado por inteiro
/// na memória (usa <see cref="Pipe"/> para ligar produtor e consumidor).
/// </summary>
public static class FolderCodec
{
    public static async Task CompressAsync(
        string folderPath,
        Stream output,
        CompressionOptions? options = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(output);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var totalBytes = CalculateTotalSize(folderPath);

        IProgress<CompressionProgress>? effectiveProgress = progress is null
            ? null
            : new Progress<CompressionProgress>(p => progress.Report(p with { TotalBytes = totalBytes }));

        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 2 * 1024 * 1024));

        var writeTask = Task.Run(() =>
        {
            try
            {
                WriteTar(pipe.Writer.AsStream(), folderPath, cancellationToken);
            }
            finally
            {
                pipe.Writer.Complete();
            }
        }, cancellationToken);

        var readTask = ChunkedCompressor.CompressAsync(
            pipe.Reader.AsStream(), output, options, effectiveProgress, cancellationToken, Zpp2Format.ContentTypeDirectory);

        await Task.WhenAll(writeTask, readTask).ConfigureAwait(false);
    }

    private static long CalculateTotalSize(string folderPath)
    {
        var dir = new DirectoryInfo(folderPath);
        long total = 0;

        foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            total += file.Length;
        }

        return total;
    }

    public static async Task ExtractAsync(
        Stream input,
        string destinationDir,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(destinationDir);

        Directory.CreateDirectory(destinationDir);

        // Extrai via arquivo temporário (stream seekable): o TarReader pode retornar
        // entradas com DataStream nulo ao ler de streams não-seekable (cabeçalhos PAX).
        var tempPath = Path.Combine(Path.GetTempPath(), $"zpp2-{Guid.NewGuid():N}.tar");
        try
        {
            await using (var temp = new FileStream(
                tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                await ChunkedDecompressor
                    .DecompressAsync(input, temp, progress, cancellationToken)
                    .ConfigureAwait(false);

                temp.Position = 0;
                await ExtractTarAsync(temp, destinationDir, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // ignora falha de limpeza do temporário
            }
        }
    }

    /// <summary>Lê o byte de tipo de conteúdo do cabeçalho sem consumir o stream (exige stream seekable).</summary>
    public static byte PeekContentType(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanSeek)
            throw new NotSupportedException("Stream must be seekable to inspect the content type.");

        var position = input.Position;
        try
        {
            Span<byte> header = stackalloc byte[14];
            input.ReadExactly(header);
            return header[13];
        }
        finally
        {
            input.Position = position;
        }
    }

    private static void WriteTar(Stream tarStream, string folderPath, CancellationToken cancellationToken)
    {
        using var tar = new TarWriter(tarStream, TarEntryFormat.Pax, leaveOpen: true);
        WriteDirectory(tar, folderPath, folderPath, cancellationToken);
    }

    private static void WriteDirectory(TarWriter tar, string dirPath, string rootPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dir = new DirectoryInfo(dirPath);

        foreach (var file in dir.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            var entryName = ToEntryName(rootPath, file.FullName);
            tar.WriteEntry(file.FullName, entryName);
        }

        foreach (var sub in dir.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            var entryName = ToEntryName(rootPath, sub.FullName) + "/";
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, entryName));
            WriteDirectory(tar, sub.FullName, rootPath, cancellationToken);
        }
    }

    private static async Task ExtractTarAsync(Stream tarStream, string destinationDir, CancellationToken cancellationToken)
    {
        using var reader = new TarReader(tarStream, leaveOpen: true);

        while (true)
        {
            var entry = await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (entry is null)
                break;

            var destPath = ResolveSafePath(destinationDir, entry.Name);

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destPath);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    await using (var fs = new FileStream(
                        destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        // DataStream é null para arquivos vazios (0 bytes).
                        if (entry.DataStream is not null)
                            await entry.DataStream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                    }
                    break;

                default:
                    // Links simbólicos, hardlinks, dispositivos etc. são ignorados por segurança.
                    break;
            }
        }
    }

    private static string ToEntryName(string rootPath, string fullPath)
        => Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');

    private static string ResolveSafePath(string destinationDir, string entryName)
    {
        var normalized = entryName.Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var full = Path.GetFullPath(Path.Combine(destinationDir, normalized));
        var baseFull = Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(baseFull, StringComparison.Ordinal))
            throw new InvalidDataException($"Path traversal detected in entry: {entryName}");

        return full;
    }
}
