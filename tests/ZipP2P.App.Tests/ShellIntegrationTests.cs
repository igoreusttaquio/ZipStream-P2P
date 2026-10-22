using Microsoft.Win32;
using ZipP2P.App.ShellIntegration;

namespace ZipP2P.App.Tests;

public sealed class ShellIntegrationTests : IDisposable
{
    private static readonly LaunchCommand Command = new("/opt/Zip Stream/ZipP2P.App", Array.Empty<string>());
    private static readonly LaunchCommand OtherCommand = new("/somewhere/else/ZipP2P.App", Array.Empty<string>());

    private readonly string _home = Directory.CreateTempSubdirectory("zpp2-home-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void Linux_InstallsForEveryFileManager_AndUninstallsCleanly()
    {
        var shell = new LinuxShellIntegration(_home);
        Assert.Equal(ShellIntegrationStatus.NotInstalled, shell.GetStatus(Command));

        shell.Install(Command, "Enviar com ZipStream");

        var nautilus = Path.Combine(_home, ".local/share/nautilus/scripts/Enviar com ZipStream");
        Assert.True(File.Exists(nautilus));
        Assert.True(File.Exists(Path.Combine(_home, ".config/caja/scripts/Enviar com ZipStream")));
        Assert.True(File.Exists(Path.Combine(_home, ".local/share/nemo/actions/zipstream-send.nemo_action")));
        Assert.True(File.Exists(Path.Combine(_home, ".local/share/kio/servicemenus/zipstream-send.desktop")));
        Assert.Contains("exec '/opt/Zip Stream/ZipP2P.App' --send \"$(readlink -f \"$1\")\"", File.ReadAllText(nautilus));
        Assert.Equal(ShellIntegrationStatus.Installed, shell.GetStatus(Command));
        Assert.Equal(ShellIntegrationStatus.Outdated, shell.GetStatus(OtherCommand));

        // Um script do usuário na mesma pasta não pode ser apagado.
        var userScript = Path.Combine(_home, ".local/share/nautilus/scripts/my-script");
        File.WriteAllText(userScript, "#!/bin/sh\necho hi\n");

        shell.Uninstall();

        Assert.Equal(ShellIntegrationStatus.NotInstalled, shell.GetStatus(Command));
        Assert.False(File.Exists(nautilus));
        Assert.True(File.Exists(userScript));
    }

    [Fact]
    public void Linux_DesktopEntries_QuoteTheExecutable()
    {
        var command = new LaunchCommand("/home/me/apps/$weird \"name\"/ZipP2P.App", Array.Empty<string>());

        var serviceMenu = LinuxShellIntegration.BuildServiceMenu(command, "Send with ZipStream");

        Assert.Contains("Exec=\"/home/me/apps/\\$weird \\\"name\\\"/ZipP2P.App\" --send %f", serviceMenu);
        Assert.Contains("MimeType=all/allfiles;inode/directory;", serviceMenu);
    }

    [Fact]
    public void Mac_CreatesQuickActionWorkflow()
    {
        var shell = new MacShellIntegration(_home);

        shell.Install(Command, "Send with ZipStream");

        var contents = Path.Combine(_home, "Library/Services", MacShellIntegration.WorkflowName, "Contents");
        Assert.Contains("<string>Send with ZipStream</string>", File.ReadAllText(Path.Combine(contents, "Info.plist")));
        var document = File.ReadAllText(Path.Combine(contents, "document.wflow"));
        Assert.Contains("'/opt/Zip Stream/ZipP2P.App' --send \"$f\" &gt;/dev/null 2&gt;&amp;1 &amp;", document);
        Assert.Equal(ShellIntegrationStatus.Installed, shell.GetStatus(Command));
        Assert.Equal(ShellIntegrationStatus.Outdated, shell.GetStatus(OtherCommand));

        shell.Uninstall();
        Assert.Equal(ShellIntegrationStatus.NotInstalled, shell.GetStatus(Command));
    }

    [Fact]
    public void Windows_RegistersVerbForFilesAndFolders()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = $@"Software\ZipStreamTests\{Guid.NewGuid():N}";
        var shell = new WindowsShellIntegration(root);
        var command = new LaunchCommand(@"C:\Program Files\ZipStream\ZipP2P.App.exe", Array.Empty<string>());
        try
        {
            shell.Install(command, "Enviar com ZipStream");

            foreach (var target in new[] { "*", "Directory" })
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{root}\{target}\shell\{WindowsShellIntegration.VerbName}");
                Assert.NotNull(key);
                Assert.Equal("Enviar com ZipStream", key.GetValue(null));
                using var commandKey = key.OpenSubKey("command");
                Assert.Equal("\"C:\\Program Files\\ZipStream\\ZipP2P.App.exe\" \"--send\" \"%1\"", commandKey!.GetValue(null));
            }

            Assert.Equal(ShellIntegrationStatus.Installed, shell.GetStatus(command));
            Assert.Equal(ShellIntegrationStatus.Outdated, shell.GetStatus(new LaunchCommand(@"D:\old\ZipP2P.App.exe", Array.Empty<string>())));

            shell.Uninstall();
            Assert.Equal(ShellIntegrationStatus.NotInstalled, shell.GetStatus(command));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\ZipStreamTests", writable: true);
            if (parent is { SubKeyCount: 0 })
                Registry.CurrentUser.DeleteSubKey(@"Software\ZipStreamTests");
        }
    }

    [Fact]
    public void DotnetHost_IncludesTheDllInTheCommand()
    {
        var command = new LaunchCommand("/usr/bin/dotnet", new[] { "/app/ZipP2P.App.dll" });

        Assert.Equal(new[] { "/app/ZipP2P.App.dll", "--send", "x" }, command.ArgumentsFor("x"));
    }
}
