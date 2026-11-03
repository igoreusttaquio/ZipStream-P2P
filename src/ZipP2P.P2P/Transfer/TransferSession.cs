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
/// <item>Só entre dispositivos pareados (<see cref="SessionAuthentication"/>): ambos enviam <c>Identity</c>
/// (cifrado): a chave pública da identidade e a assinatura das chaves efêmeras.</item>
/// <item>Remetente envia <c>Header</c> (cifrado): id, nome, tamanho e tipo do pacote. Se o pacote ainda
/// está sendo compactado, o tamanho vai como -1 (com uma estimativa) e o final chega em <c>End</c>.</item>
/// <item>Destinatário responde <c>ResumeOffset</c>: quantos bytes desse pacote já possui.</item>
/// <item>Remetente envia <c>Chunk</c>s (cifrados) a partir do offset, com no máximo
/// <see cref="WindowSize"/> bytes sem confirmação, e <c>End</c> com o tamanho se ele era desconhecido.</item>
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
    private const byte TypeEnd = 0x08;
    private const byte TypeIdentity = 0x09;

    private readonly ITransport _transport;
    private readonly ILogger _logger;
    private readonly TimeSpan _idleTimeout;
    private readonly SessionAuthentication? _authentication;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <param name="authentication">
    /// Se informado, o par precisa provar uma identidade confiável (receive mode); senão a sessão é
    /// anônima e a verificação fica com o código SAS.
    /// </param>
    public TransferSession(
        ITransport transport,
        ILogger? logger = null,
        TimeSpan? idleTimeout = null,
        SessionAuthentication? authentication = null)
    {
        _transport = transport;
        _logger = logger ?? NullLogger.Instance;
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        _authentication = authentication;
        _transport.MessageReceived += m => _incoming.Writer.TryWrite(m);
        _transport.Disconnected += () =>
            _incoming.Writer.TryComplete(new ConnectionLostException("The peer connection was closed."));
    }

    public ITransport Transport => _transport;

    /// <summary>Disparado quando o código de verificação (SAS) é derivado — exibir para o usuário conferir.</summary>
    public event Action<string>? SasReady;

    /// <summary>Chave pública da identidade do par, depois de autenticado (só com <see cref="SessionAuthentication"/>).</summary>
    public byte[]? PeerIdentityKey { get; private set; }

    /// <summary>Envia o pacote já comprimido em <paramref name="packagePath"/>, retomando do offset informado pelo destinatário.</summary>
    public Task SendAsync(
        string packagePath,
        TransferHeader header,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => SendAsync(PackageSource.FromFile(packagePath), header, progress, cancellationToken);

    /// <summary>
    /// Envia o pacote, que pode ainda estar sendo compactado, retomando do offset informado pelo
    /// destinatário. O tamanho e a estimativa do cabeçalho vêm do <paramref name="package"/>.
    /// </summary>
    public async Task SendAsync(
        PackageSource package,
        TransferHeader header,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cipher = await EstablishKeyAsync(cancellationToken).ConfigureAwait(false);

        header = package.Length is { } length
            ? header with { Size = length, EstimatedSize = 0 }
            : header with { Size = -1, EstimatedSize = package.EstimatedLength };
        var headerJson = JsonSerializer.SerializeToUtf8Bytes(header);
        await SendTypedAsync(TypeHeader, cipher.Encrypt(headerJson), cancellationToken).ConfigureAwait(false);

        var offsetPayload = await ReadExpectedAsync(TypeResumeOffset, cancellationToken).ConfigureAwait(false);
        var offset = ReadInt64(offsetPayload);
        if (offset < 0 || (header.Size >= 0 && offset > header.Size))
            throw new InvalidDataException($"Invalid resume offset {offset} for a package of {header.Size} bytes.");

        _logger.LogInformation(
            "Sending {File} ({Size} bytes{Streaming}, id {Id}) starting at offset {Offset}",
            header.FileName, header.Size >= 0 ? header.Size : header.EstimatedSize,
            header.Size >= 0 ? string.Empty : " estimated, still compressing", header.TransferId, offset);

        // Mesmo total que o destinatário vê: a estimativa do cabeçalho até o tamanho final ser anunciado.
        var size = header.Size;
        long Total(long done) => size >= 0 ? size : Math.Max(header.EstimatedSize, done);

        var stopwatch = Stopwatch.StartNew();
        var meter = new TransferRateMeter(offset);
        meter.Report(progress, offset, Total(offset), size < 0);
        long sent = offset;
        long acked = offset;
        long reported = offset;
        var buffer = new byte[ChunkSize];

        await using (var file = package.OpenRead())
        {
            while (true)
            {
                while (sent - acked >= WindowSize)
                    acked = await WaitForAckAsync(cancellationToken).ConfigureAwait(false);

                var read = await package.ReadAsync(file, sent, buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await SendTypedAsync(TypeChunk, cipher.Encrypt(buffer.AsSpan(0, read)), cancellationToken).ConfigureAwait(false);
                sent += read;

                // Inclui as confirmações recebidas na espera da janela acima (antes ficavam sem relatório
                // e, com a janela quase sempre cheia, a barra do remetente só andava no final).
                acked = DrainAcks(acked);
                if (acked != reported)
                {
                    meter.Report(progress, acked, Total(acked), size < 0);
                    reported = acked;
                }
            }
        }

        var finalLength = package.Length!.Value;
        if (offset > finalLength || (size >= 0 && sent != size))
            throw new InvalidDataException($"The package has {finalLength} bytes, but the transfer expected {Math.Max(size, offset)}.");

        if (size < 0)
        {
            size = finalLength;
            await SendTypedAsync(TypeEnd, WriteInt64(size), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("All {Size} bytes sent; waiting for the receiver to confirm", size);

        try
        {
            meter.Report(progress, acked, size);
            while (acked < size)
            {
                acked = await WaitForAckAsync(cancellationToken).ConfigureAwait(false);
                meter.Report(progress, acked, size);
            }

            await ReadExpectedAsync(TypeDone, cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectionLostException) when (acked >= size)
        {
            // Todos os bytes já foram confirmados como gravados; só o "Done" se perdeu.
            _logger.LogWarning("Connection closed after the receiver confirmed every byte; treating as complete");
        }

        _logger.LogInformation(
            "Send completed: {Bytes} bytes in {Seconds:F1}s", size - offset, stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Recebe e decifra o pacote. <paramref name="resolvePartialPath"/> recebe o cabeçalho e devolve o
    /// arquivo onde gravar; se ele já existir, a transferência é retomada do tamanho atual.
    /// O pacote não é descomprimido aqui (ver <see cref="TransferPackage"/>).
    /// </summary>
    /// <returns>O cabeçalho, com o tamanho final do pacote.</returns>
    public async Task<TransferHeader> ReceiveAsync(
        Func<TransferHeader, string> resolvePartialPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cipher = await EstablishKeyAsync(cancellationToken).ConfigureAwait(false);

        var headerPayload = await ReadExpectedAsync(TypeHeader, cancellationToken).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<TransferHeader>(cipher.Decrypt(headerPayload.Span))
            ?? throw new InvalidDataException("Invalid transfer header.");
        if (header.Size < -1 || string.IsNullOrWhiteSpace(header.FileName))
            throw new InvalidDataException("Invalid transfer header.");

        var partialPath = resolvePartialPath(header);
        long offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (header.Size >= 0 && offset > header.Size)
        {
            _logger.LogWarning("Partial file is larger than the package ({Offset} > {Size}); restarting", offset, header.Size);
            offset = 0;
        }

        _logger.LogInformation(
            "Receiving {File} ({Size} bytes{Streaming}, id {Id}) into {Partial}, resuming at {Offset}",
            header.FileName, header.Size >= 0 ? header.Size : header.EstimatedSize,
            header.Size >= 0 ? string.Empty : " estimated, sender still compressing", header.TransferId, partialPath, offset);

        await SendTypedAsync(TypeResumeOffset, WriteInt64(offset), cancellationToken).ConfigureAwait(false);

        // -1: o remetente ainda está compactando; o tamanho final chega na mensagem End.
        var size = header.Size;
        long Total(long done) => size >= 0 ? size : Math.Max(header.EstimatedSize, done);

        var stopwatch = Stopwatch.StartNew();
        var meter = new TransferRateMeter(offset);
        meter.Report(progress, offset, Total(offset), size < 0);
        long written = offset;
        long lastAck = offset;

        await using (var file = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
        {
            file.SetLength(offset);
            file.Seek(offset, SeekOrigin.Begin);

            while (size < 0 || written < size)
            {
                var (type, payload) = await ReadMessageAsync(cancellationToken, TypeChunk, TypeEnd).ConfigureAwait(false);
                if (type == TypeEnd)
                {
                    size = ReadInt64(payload);
                    if (size != written)
                        throw new InvalidDataException($"The sender announced {size} bytes, but {written} were received.");
                    break;
                }

                var chunk = cipher.Decrypt(payload.Span);
                if (size >= 0 && written + chunk.Length > size)
                    throw new InvalidDataException("Received more data than announced in the header.");

                await file.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                written += chunk.Length;

                if (written - lastAck >= AckInterval || written == size)
                {
                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await SendTypedAsync(TypeAck, WriteInt64(written), cancellationToken).ConfigureAwait(false);
                    lastAck = written;
                    meter.Report(progress, written, Total(written), size < 0);
                }
            }

            // Tamanho anunciado só no End: falta confirmar o último trecho.
            if (lastAck != written)
            {
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                await SendTypedAsync(TypeAck, WriteInt64(written), cancellationToken).ConfigureAwait(false);
            }

            meter.Report(progress, written, size);
        }

        await SendTypedAsync(TypeDone, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Receive completed: {Bytes} bytes in {Seconds:F1}s", size - offset, stopwatch.Elapsed.TotalSeconds);

        return header with { Size = size };
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

        var cipher = new ChunkCipher(key);
        if (_authentication is not null)
            await AuthenticatePeerAsync(_authentication, agreement.PublicKey, otherKey, cipher, cancellationToken).ConfigureAwait(false);

        return cipher;
    }

    private async Task AuthenticatePeerAsync(
        SessionAuthentication authentication,
        byte[] ownEphemeralKey,
        ReadOnlyMemory<byte> peerEphemeralKey,
        ChunkCipher cipher,
        CancellationToken cancellationToken)
    {
        // Payload: [tamanho da chave (2 bytes)][chave pública da identidade][assinatura].
        var identityKey = authentication.Identity.PublicKey;
        var signature = authentication.Identity.Sign(SessionAuthentication.Transcript(ownEphemeralKey, peerEphemeralKey.Span));
        var payload = new byte[2 + identityKey.Length + signature.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)identityKey.Length);
        identityKey.CopyTo(payload.AsSpan(2));
        signature.CopyTo(payload.AsSpan(2 + identityKey.Length));
        await SendTypedAsync(TypeIdentity, cipher.Encrypt(payload), cancellationToken).ConfigureAwait(false);

        var reply = cipher.Decrypt((await ReadExpectedAsync(TypeIdentity, cancellationToken).ConfigureAwait(false)).Span);
        var keyLength = reply.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(reply) : int.MaxValue;
        if (2 + keyLength >= reply.Length)
            throw new InvalidDataException("Invalid identity message.");

        var peerIdentityKey = reply.AsSpan(2, keyLength).ToArray();
        var peerSignature = reply.AsSpan(2 + keyLength);
        if (!DeviceIdentity.Verify(peerIdentityKey, SessionAuthentication.Transcript(peerEphemeralKey.Span, ownEphemeralKey), peerSignature))
            throw new PeerNotTrustedException("The other device failed to prove its identity.");

        var peerId = DeviceIdentity.ComputeDeviceId(peerIdentityKey);
        if (!authentication.IsTrusted(peerIdentityKey))
        {
            _logger.LogWarning("Rejected device {DeviceId}: not paired", peerId);
            throw new PeerNotTrustedException("The other device is not paired with this one.");
        }

        PeerIdentityKey = peerIdentityKey;
        _logger.LogInformation("Peer authenticated as paired device {DeviceId}", peerId);
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
        => (await ReadMessageAsync(cancellationToken, expectedType).ConfigureAwait(false)).Payload;

    private async Task<(byte Type, ReadOnlyMemory<byte> Payload)> ReadMessageAsync(
        CancellationToken cancellationToken, params byte[] expectedTypes)
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
                $"No response from the peer for {_idleTimeout.TotalSeconds:F0}s (waiting for message 0x{expectedTypes[0]:X2}).");
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
        if (Array.IndexOf(expectedTypes, type) < 0)
            throw new InvalidDataException($"Unexpected message type 0x{type:X2} (expected 0x{expectedTypes[0]:X2}).");

        return (type, message[1..]);
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
