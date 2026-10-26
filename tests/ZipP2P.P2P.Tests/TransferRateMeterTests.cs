using ZipP2P.P2P.Transfer;

namespace ZipP2P.P2P.Tests;

public sealed class TransferRateMeterTests
{
    private const long MB = 1024 * 1024;

    private TimeSpan _now;
    private readonly List<TransferProgress> _reports = new();

    private TransferRateMeter CreateMeter(long initialBytes = 0) => new(initialBytes, () => _now);

    private void ReportAt(TransferRateMeter meter, double seconds, long done, long total = 1000 * MB)
    {
        _now = TimeSpan.FromSeconds(seconds);
        meter.Report(new SyncProgress<TransferProgress>(_reports.Add), done, total);
    }

    [Fact]
    public void NoRate_UntilOneSecondOfSamples()
    {
        var meter = CreateMeter();
        ReportAt(meter, 0, 0);
        ReportAt(meter, 0.5, 5 * MB);

        Assert.All(_reports, r => Assert.Equal(0, r.BytesPerSecond));
    }

    [Fact]
    public void Rate_FollowsTheLastSecondsNotTheWholeTransfer()
    {
        var meter = CreateMeter();
        // 10 s a 1 MB/s e depois 10 s a 10 MB/s: a média geral seria 5,5 MB/s.
        for (var s = 1; s <= 10; s++)
            ReportAt(meter, s, s * MB);
        for (var s = 11; s <= 20; s++)
            ReportAt(meter, s, 10 * MB + (s - 10) * 10 * MB);

        Assert.Equal(10 * MB, _reports[^1].BytesPerSecond, precision: 0);
    }

    [Fact]
    public void Resume_CountsOnlyBytesOfThisConnection()
    {
        var meter = CreateMeter(initialBytes: 500 * MB);
        ReportAt(meter, 2, 504 * MB);

        Assert.Equal(2 * MB, _reports[^1].BytesPerSecond, precision: 0);
        Assert.Equal(504 * MB, _reports[^1].BytesTransferred);
    }

    [Fact]
    public void Reports_AreThrottledButTheLastOneAlwaysGoesOut()
    {
        var meter = CreateMeter();
        for (var i = 1; i <= 10; i++)
            ReportAt(meter, i * 0.01, i * MB, total: 10 * MB);

        // 0,01 s .. 0,09 s cabem num único intervalo de 250 ms; o de 100% sai mesmo assim.
        Assert.Equal(2, _reports.Count);
        Assert.Equal(10 * MB, _reports[^1].BytesTransferred);
    }
}
