using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZipP2P.P2P.Transfer;

public enum TransferStage
{
    /// <summary>Na sala, aguardando o outro dispositivo.</summary>
    WaitingForPeer,

    /// <summary>Conexão caiu; tentando de novo.</summary>
    Reconnecting,

    /// <summary>Conectado; dados fluindo.</summary>
    Transferring,
}

/// <summary>Mudança de etapa. <paramref name="Attempt"/> conta as reconexões (0 na primeira conexão).</summary>
public sealed record TransferStatus(TransferStage Stage, int Attempt, string? Detail = null);

/// <summary>
/// Executa uma transferência sobre conexões P2P, reconectando automaticamente quando a conexão
/// cai. Como o pacote e o arquivo parcial ficam no <see cref="TransferStore"/>, cada reconexão
/// retoma de onde parou — e a transferência também pode ser retomada depois de reabrir o app.
/// </summary>
public sealed class TransferRunner
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    private readonly IPeerLinkFactory _factory;
    private readonly TransferStore _store;
    private readonly ILogger _logger;
    private readonly TimeSpan? _idleTimeout;
    private readonly Func<int, TimeSpan> _backoff;

    public TransferRunner(
        IPeerLinkFactory factory,
        TransferStore store,
        ILogger? logger = null,
        TimeSpan? idleTimeout = null,
        Func<int, TimeSpan>? backoff = null)
    {
        _factory = factory;
        _store = store;
        _logger = logger ?? NullLogger.Instance;
        _idleTimeout = idleTimeout;
        _backoff = backoff ?? (attempt => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), MaxBackoff.TotalSeconds)));
    }

    public event Action<TransferStatus>? StatusChanged;

    public event Action<string>? SasReady;

    /// <summary>Envia um pacote preparado. Ao concluir, remove-o do <see cref="TransferStore"/>.</summary>
    public async Task SendAsync(OutgoingTransfer transfer, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var header = new TransferHeader(transfer.Id, transfer.FileName, transfer.PackageSize, transfer.ContentType);
        var packagePath = _store.PackagePath(transfer.Id);

        await RunAsync(
            transfer.RoomCode,
            asOfferer: true,
            session => session.SendAsync(packagePath, header, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        _store.Delete(transfer);
    }

    /// <summary>
    /// Recebe um pacote na sala <paramref name="roomCode"/>. Se houver um recebimento pendente com o
    /// mesmo id, ele é retomado (e mantém a pasta de destino original). O pacote continua no
    /// <see cref="TransferStore"/> até o chamador extraí-lo e removê-lo.
    /// </summary>
    public async Task<IncomingTransfer> ReceiveAsync(
        string roomCode,
        string destinationDir,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        IncomingTransfer? current = null;

        await RunAsync(
            roomCode,
            asOfferer: false,
            async session =>
            {
                await session.ReceiveAsync(
                    header =>
                    {
                        current = _store.FindIncoming(header.TransferId);
                        if (current is null)
                        {
                            current = new IncomingTransfer(
                                header.TransferId,
                                TransferPackage.SanitizeFileName(header.FileName),
                                header.ContentType,
                                header.Size,
                                destinationDir,
                                roomCode,
                                DateTimeOffset.Now);
                            _store.Save(current);
                        }
                        else
                        {
                            _logger.LogInformation("Resuming pending download {Id}", header.TransferId);
                        }

                        return _store.PartialPath(current.Id);
                    },
                    progress,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return current!;
    }

    private async Task RunAsync(string roomCode, bool asOfferer, Func<TransferSession, Task> work, CancellationToken cancellationToken)
    {
        var attempt = 0;
        var everConnected = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt == 0)
                StatusChanged?.Invoke(new TransferStatus(TransferStage.WaitingForPeer, 0));

            IPeerLink link;
            try
            {
                link = await _factory.ConnectAsync(roomCode, asOfferer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && everConnected)
            {
                // Já houve conexão antes: o par pode estar reabrindo/reconectando — continua tentando.
                _logger.LogWarning("Reconnection attempt {Attempt} failed: {Message}", attempt + 1, ex.Message);
                attempt++;
                StatusChanged?.Invoke(new TransferStatus(TransferStage.Reconnecting, attempt, ex.Message));
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (ConnectionLostException ex)
            {
                // Nunca conectou mas o ICE falhou: vale tentar de novo algumas vezes.
                _logger.LogWarning("Connection attempt {Attempt} failed: {Message}", attempt + 1, ex.Message);
                if (++attempt >= 3)
                    throw;
                StatusChanged?.Invoke(new TransferStatus(TransferStage.Reconnecting, attempt, ex.Message));
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            everConnected = true;
            string? lostReason = null;
            await using (link.ConfigureAwait(false))
            {
                var session = new TransferSession(link.Transport, _logger, _idleTimeout);
                session.SasReady += sas => SasReady?.Invoke(sas);
                StatusChanged?.Invoke(new TransferStatus(TransferStage.Transferring, attempt));

                try
                {
                    await work(session).ConfigureAwait(false);
                    return;
                }
                catch (ConnectionLostException ex)
                {
                    _logger.LogWarning("Connection lost: {Message}", ex.Message);
                    lostReason = ex.Message;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Transfer cancelled by the user");
                    await session.AbortAsync("The other user cancelled the transfer.").ConfigureAwait(false);
                    throw;
                }
                catch (TransferAbortedException ex)
                {
                    _logger.LogWarning("The other device ended the transfer: {Reason}", ex.Reason);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Transfer failed");
                    await session.AbortAsync($"The other device hit an error: {ex.Message}").ConfigureAwait(false);
                    throw;
                }
            }

            attempt++;
            StatusChanged?.Invoke(new TransferStatus(TransferStage.Reconnecting, attempt, lostReason));
            await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = _backoff(attempt);
        _logger.LogInformation("Retrying in {Seconds:F0}s (attempt {Attempt})", delay.TotalSeconds, attempt);
        return Task.Delay(delay, cancellationToken);
    }
}
