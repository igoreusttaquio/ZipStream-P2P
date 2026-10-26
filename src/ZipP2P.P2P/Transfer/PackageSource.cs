using ZipP2P.Core.Compression;

namespace ZipP2P.P2P.Transfer;

/// <summary>
/// Pacote ".zpp2" a enviar, pronto ou ainda sendo gerado. O envio começa enquanto a compressão
/// acontece: o remetente lê o arquivo do cache conforme ele cresce, sem esperar o fim.
/// </summary>
/// <remarks>
/// O pacote continua sendo gravado em disco porque a retomada precisa reenviar exatamente os mesmos
/// bytes, e comprimir uma pasta de novo não gera um arquivo idêntico (datas entram no tar).
/// </remarks>
public sealed class PackageSource
{
    private readonly TaskCompletionSource<long> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancel = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _available;
    private long _sourceProcessed;
    private long _sourceTotal = -1;

    private PackageSource(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>Tamanho final; null enquanto a compressão não termina.</summary>
    public long? Length => _completion.Task.IsCompletedSuccessfully ? _completion.Task.Result : null;

    /// <summary>Bytes já gravados e prontos para leitura.</summary>
    public long Available => Interlocked.Read(ref _available);

    /// <summary>Conclui com o tamanho final; falha ou é cancelada junto com a compressão.</summary>
    public Task<long> Completion => _completion.Task;

    /// <summary>Tamanho final, ou o já gravado projetado pela taxa de compressão até aqui.</summary>
    public long EstimatedLength
    {
        get
        {
            if (Length is { } length)
                return length;

            var available = Available;
            var processed = Interlocked.Read(ref _sourceProcessed);
            var total = Interlocked.Read(ref _sourceTotal);
            if (total <= 0)
                return available;
            if (processed <= 0)
                return total;

            return Math.Max(available, (long)((double)available / processed * total));
        }
    }

    /// <summary>Pacote já pronto em disco.</summary>
    public static PackageSource FromFile(string path)
    {
        var package = new PackageSource(path);
        var length = new FileInfo(path).Length;
        package._available = length;
        package._completion.SetResult(length);
        return package;
    }

    /// <summary>
    /// Começa a comprimir <paramref name="sourcePath"/> em <paramref name="packagePath"/> em segundo plano.
    /// <paramref name="onCompleted"/> recebe o tamanho final antes de <see cref="Completion"/> concluir.
    /// </summary>
    public static PackageSource Create(
        string sourcePath,
        string packagePath,
        CompressionOptions? options = null,
        IProgress<CompressionProgress>? progress = null,
        Action<long>? onCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var package = new PackageSource(packagePath);
        var tracking = new CallbackProgress<CompressionProgress>(p =>
        {
            package.OnSourceProgress(p);
            progress?.Report(p);
        });
        package.Run(
            (output, token) => TransferPackage.CreateAsync(sourcePath, output, options, tracking, token),
            onCompleted,
            cancellationToken);
        return package;
    }

    /// <summary>Gera o pacote com um produtor qualquer (usado nos testes).</summary>
    public static PackageSource Start(
        string packagePath,
        Func<Stream, CancellationToken, Task> produce,
        Action<long>? onCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var package = new PackageSource(packagePath);
        package.Run(produce, onCompleted, cancellationToken);
        return package;
    }

    /// <summary>Interrompe a compressão (sem efeito se ela já terminou); o pacote parcial é apagado.</summary>
    public void Cancel()
    {
        if (!_completion.Task.IsCompleted)
            _cancel.Cancel();
    }

    /// <summary>Abre o pacote para leitura (pode estar sendo gravado ao mesmo tempo).</summary>
    public FileStream OpenRead()
        => new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, useAsync: true);

    /// <summary>
    /// Lê a partir de <paramref name="position"/>, esperando a compressão produzir os bytes.
    /// Retorna 0 no fim do pacote; relança o erro se a compressão falhar.
    /// </summary>
    public async ValueTask<int> ReadAsync(FileStream file, long position, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            var changed = Volatile.Read(ref _changed).Task;
            var available = Available;
            if (available > position)
            {
                file.Position = position;
                var count = (int)Math.Min(buffer.Length, available - position);
                return await file.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
            }

            if (_completion.Task.IsCompleted)
            {
                await _completion.Task.ConfigureAwait(false);
                return 0;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Run(Func<Stream, CancellationToken, Task> produce, Action<long>? onCompleted, CancellationToken cancellationToken)
    {
        // Criado já aqui para que OpenRead funcione assim que esta chamada retorna.
        // Sem buffer: cada escrita já fica visível para quem lê o arquivo ao mesmo tempo.
        var file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);

        _ = Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel.Token);
            try
            {
                await using (file)
                await using (var output = new NotifyingStream(file, OnWritten))
                {
                    await produce(output, linked.Token).ConfigureAwait(false);
                }

                var length = Available;
                onCompleted?.Invoke(length);
                _completion.TrySetResult(length);
            }
            catch (Exception ex)
            {
                TryDelete(Path);
                if (ex is OperationCanceledException canceled)
                    _completion.TrySetCanceled(canceled.CancellationToken);
                else
                    _completion.TrySetException(ex);
            }
            finally
            {
                Signal();
            }
        });
    }

    private void OnSourceProgress(CompressionProgress progress)
    {
        Interlocked.Exchange(ref _sourceTotal, progress.TotalBytes);
        Interlocked.Exchange(ref _sourceProcessed, progress.BytesProcessed);
    }

    private void OnWritten(long position)
    {
        Interlocked.Exchange(ref _available, position);
        Signal();
    }

    private void Signal()
        => Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

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

    /// <summary>IProgress que chama o callback na hora (o Progress&lt;T&gt; posta no thread pool).</summary>
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Repassa as escritas e avisa a nova posição depois de cada uma.</summary>
    private sealed class NotifyingStream(Stream inner, Action<long> written) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            written(inner.Position);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            written(inner.Position);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
