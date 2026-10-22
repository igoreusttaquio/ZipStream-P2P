using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZipP2P.Core.Security;

namespace ZipP2P.P2P.Transfer;

/// <summary>
/// Sessão de transferência de um pacote ".zpp2" em uma direção, sobre uma única conexão,
/// com criptografia ponta a ponta (E2EE), controle de fluxo e retomada (resume).
/// </summary>
/// <remarks>
/// Protocolo (cada mensagem = 1 byte de tipo + payload):
/// <list type="number">
/// <item>Ambos enviam <c>PublicKey</c> (ECDH) e derivam a chave AES-GCM + código SAS.</item>
/// <item>Remetente envia <c>Header</c> (cifrado): id, nome, tamanho e tipo do pacote.</item>
/// <item>Destinatário responde <c>ResumeOffset</c>: quantos bytes desse pacote já possui.</item>
/// <item>Remetente envia <c>Chunk</c>s (cifrados) a partir do offset, com no máximo
/// <see cref="WindowSize"/> bytes sem confirmação.</item>
/// <item>Destinatário confirma com <c>Ack</c> (bytes gravados em disco) e, ao final, <c>Done</c>.</item>
/// </list>
/// Qualquer lado pode enviar <c>Abort</c> com o motivo antes de encerrar.
/// Sem controle de fluxo e sem o <c>Done</c>, o remetente fechava a conexão com dados ainda no
/// buffer do libdatachannel, e arquivos grandes chegavam incompletos.
/// </remarks>
public sealed class TransferSession : IAsyncDisposable
{
    /// <summary>Payload de cada chunk. Com tipo + nonce + tag fica abaixo de 64 KiB (limite comum de mensagem SCTP).</summary>
    public const int ChunkSize = 60 * 1024;

    /// <summary>Máximo de bytes enviados e ainda não confirmados pelo destinatário.</summary>
    public const long WindowSize = 8 * 1024 * 1024;

    private const long AckInterval = 1024 * 1024;

    private const byte TypePublicKey = 0x01;
    private const byte TypeHeader = 0x02;
    private const byte TypeChunk = 0x03;
    private const byte TypeResumeOffset = 0x04;
    private const byte TypeAck = 0x05;
    private const byte TypeDone = 0x06;
    private const byte TypeAbort = 0x07;

    private readonly ITransport _transport;
    private readonly ILogger _logger;
    private readonly TimeSpan _idleTimeout;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public TransferSession(ITransport transport, ILogger? logger = null, TimeSpan? idleTimeout = null)
    {
        _transport = transport;
        _logger = logger ?? NullLogger.Instance;
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        _transport.MessageReceived += m => _incoming.Writer.TryWrite(m);
        _transport.Disconnected += () =>
            _incoming.Writer.TryComplete(new ConnectionLostException("The peer connection was closed."));
    }

    public ITransport Transport => _transport;

    /// <summary>Disparado quando o código de verificação (SAS) é derivado — exibir para o usuário conferir.</summary>
    public event Action<string>? SasReady;

    /// <summary>Envia o pacote já comprimido em <paramref name="packagePath"/>, retomando do offset informado pelo destinatário.</summary>
    public async Task SendAsync(
        string packagePath,
        TransferHeader header,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cipher = await EstablishKeyAsync(cancellationToken).ConfigureAwait(false);

        var headerJson = JsonSerializer.SerializeToUtf8Bytes(header);
        await SendTypedAsync(TypeHeader, cipher.Encrypt(headerJson), cancellationToken).ConfigureAwait(false);

        var offsetPayload = await ReadExpectedAsync(TypeResumeOffset, cancellationToken).ConfigureAwait(false);
        var offset = ReadInt64(offsetPayload);
        if (offset < 0 || offset > header.Size)
            throw new InvalidDataException($"Invalid resume offset {offset} for a package of {header.Size} bytes.");

        _logger.LogInformation(
            "Sending {File} ({Size} bytes, id {Id}) starting at offset {Offset}",
            header.FileName, header.Size, header.TransferId, offset);

        var stopwatch = Stopwatch.StartNew();
        long sent = offset;
        long acked = offset;
        var buffer = new byte[ChunkSize];

        await using (var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            file.Seek(offset, SeekOrigin.Begin);

            while (sent < header.Size)
            {
                while (sent - acked >= WindowSize)
                    acked = await WaitForAckAsync(cancellationToken).ConfigureAwait(false);

                var toRead = (int)Math.Min(buffer.Length, header.Size - sent);
                var read = await file.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new InvalidDataException("The package file ended before the expected size.");

                await SendTypedAsync(TypeChunk, cipher.Encrypt(buffer.AsSpan(0, read)), cancellationToken).ConfigureAwait(false);
                sent += read;

                var previous = acked;
                acked = DrainAcks(acked);
                if (acked != previous)
                    Report(progress, acked - offset, acked, header.Size, stopwatch);
            }
        }

        _logger.LogDebug("All {Size} bytes sent; waiting for the receiver to confirm", header.Size);

        try
        {
            while (acked < header.Size)
            {
                acked = await WaitForAckAsync(cancellationToken).ConfigureAwait(false);
                Report(progress, acked - offset, acked, header.Size, stopwatch);
            }

            await ReadExpectedAsync(TypeDone, cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectionLostException) when (acked >= header.Size)
        {
            // Todos os bytes já foram confirmados como gravados; só o "Done" se perdeu.
            _logger.LogWarning("Connection closed after the receiver confirmed every byte; treating as complete");
        }

        _logger.LogInformation(
            "Send completed: {Bytes} bytes in {Seconds:F1}s", header.Size - offset, stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Recebe e decifra o pacote. <paramref name="resolvePartialPath"/> recebe o cabeçalho e devolve o
    /// arquivo onde gravar; se ele já existir, a transferência é retomada do tamanho atual.
    /// O pacote não é descomprimido aqui (ver <see cref="TransferPackage"/>).
    /// </summary>
    public async Task<TransferHeader> ReceiveAsync(
        Func<TransferHeader, string> resolvePartialPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cipher = await EstablishKeyAsync(cancellationToken).ConfigureAwait(false);

        var headerPayload = await ReadExpectedAsync(TypeHeader, cancellationToken).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<TransferHeader>(cipher.Decrypt(headerPayload.Span))
            ?? throw new InvalidDataException("Invalid transfer header.");
        if (header.Size < 0 || string.IsNullOrWhiteSpace(header.FileName))
            throw new InvalidDataException("Invalid transfer header.");

        var partialPath = resolvePartialPath(header);
        long offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (offset > header.Size)
        {
            _logger.LogWarning("Partial file is larger than the package ({Offset} > {Size}); restarting", offset, header.Size);
            offset = 0;
        }

        _logger.LogInformation(
            "Receiving {File} ({Size} bytes, id {Id}) into {Partial}, resuming at {Offset}",
            header.FileName, header.Size, header.TransferId, partialPath, offset);

        await SendTypedAsync(TypeResumeOffset, WriteInt64(offset), cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        long written = offset;
        long lastAck = offset;

        await using (var file = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
        {
            file.SetLength(offset);
            file.Seek(offset, SeekOrigin.Begin);

            while (written < header.Size)
            {
                var encrypted = await ReadExpectedAsync(TypeChunk, cancellationToken).ConfigureAwait(false);
                var chunk = cipher.Decrypt(encrypted.Span);
                if (written + chunk.Length > header.Size)
                    throw new InvalidDataException("Received more data than announced in the header.");

                await file.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                written += chunk.Length;

                if (written - lastAck >= AckInterval || written == header.Size)
                {
                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await SendTypedAsync(TypeAck, WriteInt64(written), cancellationToken).ConfigureAwait(false);
                    lastAck = written;
                    Report(progress, written - offset, written, header.Size, stopwatch);
                }
            }
        }

        await SendTypedAsync(TypeDone, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Receive completed: {Bytes} bytes in {Seconds:F1}s", header.Size - offset, stopwatch.Elapsed.TotalSeconds);

        return header;
    }

    /// <summary>Avisa o par que a transferência foi encerrada (melhor esforço; nunca lança).</summary>
    public async Task AbortAsync(string reason)
    {
        try
        {
            if (!_transport.IsConnected)
                return;

            await SendTypedAsync(TypeAbort, Encoding.UTF8.GetBytes(reason), CancellationToken.None).ConfigureAwait(false);
            // Dá tempo para a mensagem sair do buffer antes de a conexão ser fechada.
            await Task.Delay(300).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not notify the peer about the abort");
        }
    }

    private async Task<ChunkCipher> EstablishKeyAsync(CancellationToken cancellationToken)
    {
        using var agreement = new KeyAgreement();
        await SendTypedAsync(TypePublicKey, agreement.PublicKey, cancellationToken).ConfigureAwait(false);

        var otherKey = await ReadExpectedAsync(TypePublicKey, cancellationToken).ConfigureAwait(false);
        var key = agreement.DeriveKey(otherKey.Span);

        var sas = SasCode.Compute(agreement.PublicKey, otherKey.Span, key);
        _logger.LogInformation("Key agreement completed (ECDH P-256); security code {Sas}", sas);
        SasReady?.Invoke(sas);

        return new ChunkCipher(key);
    }

    private async Task<long> WaitForAckAsync(CancellationToken cancellationToken)
    {
        var payload = await ReadExpectedAsync(TypeAck, cancellationToken).ConfigureAwait(false);
        return ReadInt64(payload);
    }

    /// <summary>Consome, sem bloquear, as confirmações já recebidas.</summary>
    private long DrainAcks(long acked)
    {
        while (_incoming.Reader.TryPeek(out var message) && message.Length > 0)
        {
            var type = message.Span[0];
            if (type == TypeAbort)
                throw new TransferAbortedException(Encoding.UTF8.GetString(message.Span[1..]));
            if (type != TypeAck)
                break;

            _incoming.Reader.TryRead(out _);
            acked = ReadInt64(message[1..]);
        }

        return acked;
    }

    private async Task SendTypedAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var message = new byte[1 + payload.Length];
        message[0] = type;
        payload.CopyTo(message.AsMemory(1));

        try
        {
            await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConnectionLostException($"Failed to send data to the peer: {ex.Message}", ex);
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadExpectedAsync(byte expectedType, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_idleTimeout);

        ReadOnlyMemory<byte> message;
        try
        {
            message = await _incoming.Reader.ReadAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectionLostException(
                $"No response from the peer for {_idleTimeout.TotalSeconds:F0}s (waiting for message 0x{expectedType:X2}).");
        }
        catch (ChannelClosedException ex)
        {
            throw ex.InnerException as ConnectionLostException
                ?? new ConnectionLostException("The peer connection was closed.", ex);
        }

        if (message.Length < 1)
            throw new InvalidDataException("Empty message received.");

        var type = message.Span[0];
        if (type == TypeAbort)
            throw new TransferAbortedException(Encoding.UTF8.GetString(message.Span[1..]));
        if (type != expectedType)
            throw new InvalidDataException($"Unexpected message type 0x{type:X2} (expected 0x{expectedType:X2}).");

        return message[1..];
    }

    private static void Report(IProgress<TransferProgress>? progress, long bytesThisConnection, long done, long total, Stopwatch stopwatch)
    {
        if (progress is null)
            return;

        var seconds = stopwatch.Elapsed.TotalSeconds;
        progress.Report(new TransferProgress(done, total, seconds > 0 ? bytesThisConnection / seconds : 0));
    }

    private static byte[] WriteInt64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static long ReadInt64(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length != 8)
            throw new InvalidDataException("Invalid 64-bit value in message.");

        return BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
