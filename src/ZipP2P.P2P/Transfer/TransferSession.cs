using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ZipP2P.Core.Compression;

namespace ZipP2P.P2P.Transfer;

/// <summary>
/// Sessão de transferência em uma direção: compacta e envia (lado remetente) ou
/// recebe e descomprime (lado destinatário). Inscreve-se no transporte no construtor,
/// para não perder mensagens que cheguem antes do início da recepção.
/// </summary>
public sealed class TransferSession : IAsyncDisposable
{
    private const int ChunkSize = 64 * 1024;

    private readonly ITransport _transport;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public TransferSession(ITransport transport)
    {
        _transport = transport;
        _transport.MessageReceived += m => _incoming.Writer.TryWrite(m);
    }

    public ITransport Transport => _transport;

    /// <summary>Compacta <paramref name="inputPath"/> (arquivo ou pasta) e envia pelo canal.</summary>
    public async Task SendAsync(string inputPath, IProgress<CompressionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var isFolder = Directory.Exists(inputPath);
        var contentType = isFolder ? Zpp2Format.ContentTypeDirectory : Zpp2Format.ContentTypeFile;
        var fileName = Path.GetFileName(inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var tempPath = Path.Combine(Path.GetTempPath(), $"zpp2-send-{Guid.NewGuid():N}.zpp2");
        try
        {
            await using (var output = File.Create(tempPath))
            {
                if (isFolder)
                    await FolderCodec.CompressAsync(inputPath, output, null, progress, cancellationToken).ConfigureAwait(false);
                else
                {
                    await using var input = File.OpenRead(inputPath);
                    await ChunkedCompressor.CompressAsync(input, output, null, progress, cancellationToken).ConfigureAwait(false);
                }
            }

            var size = new FileInfo(tempPath).Length;
            var header = JsonSerializer.Serialize(new TransferHeader(fileName, size, contentType));
            await _transport.SendAsync(Encoding.UTF8.GetBytes(header), cancellationToken).ConfigureAwait(false);

            var buffer = new byte[ChunkSize];
            await using var fileStream = File.OpenRead(tempPath);
            int read;
            while ((read = await fileStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                await _transport.SendAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
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

    /// <summary>Recebe e descomprime para <paramref name="destinationDir"/>.</summary>
    public async Task ReceiveAsync(string destinationDir, IProgress<CompressionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDir);

        var headerMessage = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<TransferHeader>(headerMessage.Span)
            ?? throw new InvalidDataException("Invalid transfer header.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"zpp2-recv-{Guid.NewGuid():N}.zpp2");
        try
        {
            long written = 0;
            await using (var temp = File.Create(tempPath))
            {
                while (written < header.Size)
                {
                    var chunk = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    await temp.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                    written += chunk.Length;
                }
            }

            await using var input = File.OpenRead(tempPath);
            if (header.ContentType == Zpp2Format.ContentTypeDirectory)
            {
                await FolderCodec.ExtractAsync(input, destinationDir, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var destFile = Path.Combine(destinationDir, header.FileName);
                await using var output = File.Create(destFile);
                await ChunkedDecompressor.DecompressAsync(input, output, progress, cancellationToken).ConfigureAwait(false);
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

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
