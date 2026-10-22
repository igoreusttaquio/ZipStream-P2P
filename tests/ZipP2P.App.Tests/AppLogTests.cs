using Microsoft.Extensions.Logging;
using ZipP2P.App.Logging;

namespace ZipP2P.App.Tests;

public sealed class AppLogTests
{
    [Fact]
    public void Log_KeepsOnlyTheMostRecentEntries()
    {
        var log = AppLog.Instance;
        log.Clear();
        var logger = log.CreateLogger("Test");

        for (var i = 0; i < AppLog.Capacity + 10; i++)
            logger.LogInformation("entry {Index}", i);

        var entries = log.Snapshot();
        Assert.Equal(AppLog.Capacity, entries.Count);
        Assert.Equal("entry 10", entries[0].Message);
        Assert.Equal($"entry {AppLog.Capacity + 9}", entries[^1].Message);
    }

    [Fact]
    public void Export_IncludesEnvironment_Exceptions_AndFiltersDebug()
    {
        var log = AppLog.Instance;
        log.Clear();
        var logger = log.CreateLogger("Transfer");
        logger.LogDebug("ICE candidate xyz");
        logger.LogError(new InvalidOperationException("boom"), "Operation failed");

        var summary = log.Export(includeDebug: false);
        var full = log.Export(includeDebug: true);

        Assert.Contains("ZipStream", summary);
        Assert.Contains("[ERROR] Transfer: Operation failed", summary);
        Assert.Contains("System.InvalidOperationException: boom", summary);
        Assert.DoesNotContain("ICE candidate", summary);
        Assert.Contains("[DEBUG] Transfer: ICE candidate xyz", full);
    }

    [Fact]
    public void LoggerFactory_RoutesLibraryLogsToTheAppLog()
    {
        AppLog.Instance.Clear();
        AppLog.Instance.Factory.CreateLogger("WebRTC").LogWarning("connection failed");

        Assert.Contains(AppLog.Instance.Snapshot(), e => e.Category == "WebRTC" && e.IsWarning);
    }
}
