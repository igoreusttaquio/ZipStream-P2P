using ZipP2P.App.Devices;
using ZipP2P.App.ShellIntegration;

namespace ZipP2P.App.Tests;

public sealed class NotificationTests
{
    [Fact]
    public void Linux_PassesTitleAndMessageAsSeparateArguments()
    {
        var args = DesktopNotifier.LinuxArguments("Arquivo recebido", "foto \"1\".jpg, de Notebook");

        Assert.Equal(["--app-name=ZipStream", "--icon=zipstream", "Arquivo recebido", "foto \"1\".jpg, de Notebook"], args);
    }

    [Fact]
    public void Mac_EscapesQuotesAndBackslashesInTheScript()
    {
        var args = DesktopNotifier.MacArguments("Recebido", "a \"b\" c\\d");

        Assert.Equal("-e", args[0]);
        Assert.Equal("display notification \"a \\\"b\\\" c\\\\d\" with title \"Recebido\"", args[1]);
    }

    [Fact]
    public void Received_Paired_AndFailed_AreNotified_ButReceivingIsNot()
    {
        var received = ReceiveModeNotification.For(new ReceiveModeEvent(
            ReceiveModeEventKind.Received, "relatorio.pdf", "Notebook", Path.Combine("D:", "Recebidos", "relatorio.pdf")));
        var paired = ReceiveModeNotification.For(new ReceiveModeEvent(ReceiveModeEventKind.Paired, "Notebook", "Notebook"));
        var failed = ReceiveModeNotification.For(new ReceiveModeEvent(ReceiveModeEventKind.Failed, "", null, Error: "timeout"));

        Assert.NotNull(received);
        Assert.Contains("relatorio.pdf", received.Message);
        Assert.Contains("Notebook", received.Message);
        Assert.Contains("Notebook", paired!.Message);
        Assert.Equal("timeout", failed!.Message);
        Assert.Null(ReceiveModeNotification.For(new ReceiveModeEvent(ReceiveModeEventKind.Receiving, "", "Notebook")));
    }
}
