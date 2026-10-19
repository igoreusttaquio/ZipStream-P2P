using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ZipP2P.Core.Compression;
using ZipP2P.Core.Security;

namespace ZipP2P.P2P.Transfer;

/// <summary>
/// Sessão de transferência em uma direção com criptografia ponta a ponta (E2EE).
/// Compacta e envia (remetente) ou recebe e descomprime (destinatário).
/// A chave de sessão é negociada via ECDH sobre o próprio DataChannel, e cada
/// chunk (e o cabeçalho) é cifrado com AES-256-GCM — o relay/servidor nunca vê o conteúdo.
/// </summary>
public sealed class TransferSession : IAsyncDisposable
{
    private const int ChunkSize = 64 * 1024;

    private const byte TypePublicKey = 0x01;
    private const byte TypeHeader = 0x02;
    private const byte TypeChunk = 0x03;

    private readonly ITransport _transport;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public TransferSession(ITransport transport)
    {
        _transport = transport;
        _transport.MessageReceived += m => _incoming.Writer.TryWrite(m);
    }

    public ITransport Transport => _transport;

    /// <summary>Disparado quando o código de verificação (SAS) é derivado — exibir para o usuário conferir.</summary>
    public event Action<string>? SasReady;

    /// <summary>Compacta <paramref name="inputPath"/> (arquivo ou pasta) e envia cifrado.</summary>
    public async Task SendAsync(string inputPath, IProgress<CompressionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var cipher = await EstablishKeyAsync(cancellationToken).ConfigureAwait(false);

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
            await SendTypedAsync(TypeHeader, cipher.Encrypt(Encoding.UTF8.GetBytes(header)), cancellationToken).ConfigureAwait(false);

            var buffer = new byte[ChunkSize];
            await using var fileStream = File.OpenRead(tempPath);
            int read;
            while ((read = await fileStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                await SendTypedAsync(TypeChunk, cipher.Encrypt(buffer.AsSpan(0, read)), cancellationToken).ConfigureAwait(false);
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

    /// <summary>Recebe, decifra e descomprime para <paramref name="destinationDir"/>.</summary>
    public async Task ReceiveAsync(string destinationDir, IProgress<CompressionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDir);

        var cipher = await EstablishKeyAsync(cancellationToken).ConfigureAwait(false);

        var headerBytes = await ReadTypedAsync(TypeHeader, cancellationToken).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<TransferHeader>(cipher.Decrypt(headerBytes.Span))
            ?? throw new InvalidDataException("Invalid transfer header.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"zpp2-recv-{Guid.NewGuid():N}.zpp2");
        try
        {
            long written = 0;
            await using (var temp = File.Create(tempPath))
            {
                while (written < header.Size)
                {
                    var encrypted = await ReadTypedAsync(TypeChunk, cancellationToken).ConfigureAwait(false);
                    var chunk = cipher.Decrypt(encrypted.Span);
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

    private async Task<ChunkCipher> EstablishKeyAsync(CancellationToken cancellationToken)
    {
        using var agreement = new KeyAgreement();
        await SendTypedAsync(TypePublicKey, agreement.PublicKey, cancellationToken).ConfigureAwait(false);

        var otherKey = await ReadTypedAsync(TypePublicKey, cancellationToken).ConfigureAwait(false);
        var key = agreement.DeriveKey(otherKey.Span);

        SasReady?.Invoke(SasCode.Compute(agreement.PublicKey, otherKey.Span, key));

        return new ChunkCipher(key);
    }

    private async Task SendTypedAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var message = new byte[1 + payload.Length];
        message[0] = type;
        payload.CopyTo(message.AsMemory(1));
        await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReadOnlyMemory<byte>> ReadTypedAsync(byte expectedType, CancellationToken cancellationToken)
    {
        var message = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (message.Length < 1 || message.Span[0] != expectedType)
            throw new InvalidDataException($"Unexpected message type 0x{message.Span[0]:X2} (expected 0x{expectedType:X2}).");

        return message[1..];
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
