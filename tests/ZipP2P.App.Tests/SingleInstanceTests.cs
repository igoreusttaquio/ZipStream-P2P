namespace ZipP2P.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task WithoutARunningInstance_SendReturnsFalse()
    {
        var instance = new SingleInstance("ZipStreamTest-" + Guid.NewGuid().ToString("N"));

        Assert.False(await instance.TrySendAsync(new StartupCommand("x", null), TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task SecondInstance_ForwardsItsCommands_ToTheFirst()
    {
        var name = "ZipStreamTest-" + Guid.NewGuid().ToString("N");
        var received = new List<StartupCommand>();
        var gotTwo = new TaskCompletionSource();
        using var first = new SingleInstance(name);
        first.Listen(command =>
        {
            lock (received)
            {
                received.Add(command);
                if (received.Count == 2)
                    gotTwo.TrySetResult();
            }
        });

        var second = new SingleInstance(name);
        Assert.True(await second.TrySendAsync(new StartupCommand(@"C:\Users\me\Relatório.pdf", null), TimeSpan.FromSeconds(5)));
        Assert.True(await second.TrySendAsync(new StartupCommand(null, "ABC123"), TimeSpan.FromSeconds(5)));

        await gotTwo.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(@"C:\Users\me\Relatório.pdf", received[0].SendPath);
        Assert.Equal("ABC123", received[1].ReceiveCode);
    }

    [Fact]
    public void RelativeSendPath_IsResolvedBeforeForwarding()
    {
        var command = new StartupCommand("relatorio.pdf", null).WithAbsolutePaths();

        Assert.Equal(Path.GetFullPath("relatorio.pdf"), command.SendPath);
        Assert.True(Path.IsPathRooted(command.SendPath));
    }
}
