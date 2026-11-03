using Microsoft.Win32;
using ZipP2P.App.ShellIntegration;

namespace ZipP2P.App.Tests;

public sealed class AutostartTests : IDisposable
{
    private static readonly LaunchCommand Command = new("/opt/Zip Stream/ZipP2P.App", Array.Empty<string>());

    private readonly string _home = Directory.CreateTempSubdirectory("zpp2-autostart-").FullName;
    private readonly string _runKey = @"Software\ZipStreamTests\Run-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        if (OperatingSystem.IsWindows())
            Registry.CurrentUser.DeleteSubKeyTree(_runKey, throwOnMissingSubKey: false);
    }

    [Fact]
    public void EnableAndDisable_OnTheCurrentSystem()
    {
        var autostart = new Autostart(_home, _runKey);
        Assert.False(autostart.IsEnabled);

        autostart.Enable(Command);
        Assert.True(autostart.IsEnabled);
        if (OperatingSystem.IsWindows())
        {
            var value = Registry.CurrentUser.OpenSubKey(_runKey)!.GetValue(Autostart.WindowsValueName);
            Assert.Equal("\"/opt/Zip Stream/ZipP2P.App\" --background", value);
        }

        autostart.Disable();
        Assert.False(autostart.IsEnabled);
        autostart.Disable(); // de novo: não falha
    }

    [Fact]
    public void LinuxEntry_QuotesTheExecutable_AndStartsInBackground()
    {
        var entry = Autostart.BuildLinuxDesktopEntry("/opt/Zip Stream/ZipP2P.App", ["--background"]);

        Assert.Contains("Exec=\"/opt/Zip Stream/ZipP2P.App\" --background\n", entry);
        Assert.Contains("Type=Application", entry);
        Assert.Contains("X-GNOME-Autostart-enabled=true", entry);
    }

    [Fact]
    public void MacAgent_RunsAtLoad_WithEscapedArguments()
    {
        var plist = Autostart.BuildMacAgent("/Applications/Zip & Stream.app/Contents/MacOS/ZipP2P.App", ["--background"]);

        Assert.Contains("<string>com.zipstream.app</string>", plist);
        Assert.Contains("<string>/Applications/Zip &amp; Stream.app/Contents/MacOS/ZipP2P.App</string>", plist);
        Assert.Contains("<string>--background</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        System.Xml.Linq.XDocument.Parse(plist); // XML válido
    }

    [Fact]
    public void BackgroundArgument_StartsOnlyInTheTray()
    {
        var command = StartupCommand.Parse(["--background"]);

        Assert.True(command.Background);
        Assert.Null(command.SendPath);
        Assert.False(command.CloseForUpdate);
    }
}
