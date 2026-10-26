using System.Diagnostics;

namespace ZipP2P.P2P.Transfer;

/// <summary>
/// Relata o progresso com a vazão dos últimos segundos (média móvel), igual nos dois lados:
/// ambos medem os bytes já gravados pelo destinatário. A média desde o início da conexão
/// demorava a refletir a velocidade real e o tempo restante divergia entre os computadores.
/// Também limita a frequência dos relatórios para não inundar a UI.
/// </summary>
public sealed class TransferRateMeter
{
    /// <summary>Janela da média móvel.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>Tempo mínimo de amostras antes de informar uma vazão (evita estimativas absurdas no início).</summary>
    public static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(1);

    /// <summary>Intervalo mínimo entre relatórios (o último, com tudo transferido, sempre sai).</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();
    private readonly Func<TimeSpan> _clock;
    private TimeSpan? _lastReport;

    /// <param name="initialBytes">Bytes já existentes no início (retomada).</param>
    /// <param name="clock">Relógio (troca-se nos testes).</param>
    public TransferRateMeter(long initialBytes, Func<TimeSpan>? clock = null)
    {
        var stopwatch = Stopwatch.StartNew();
        _clock = clock ?? (() => stopwatch.Elapsed);
        _samples.Enqueue((_clock(), initialBytes));
    }

    public void Report(IProgress<TransferProgress>? progress, long done, long total)
    {
        var now = _clock();
        _samples.Enqueue((now, done));
        while (_samples.Count > 2 && now - _samples.Peek().Time > Window)
            _samples.Dequeue();

        if (progress is null || (done < total && now - _lastReport < ReportInterval))
            return;

        _lastReport = now;
        progress.Report(new TransferProgress(done, total, BytesPerSecond(now, done)));
    }

    private double BytesPerSecond(TimeSpan now, long done)
    {
        var (oldestTime, oldestBytes) = _samples.Peek();
        var span = now - oldestTime;
        return span < MinSpan ? 0 : (done - oldestBytes) / span.TotalSeconds;
    }
}
