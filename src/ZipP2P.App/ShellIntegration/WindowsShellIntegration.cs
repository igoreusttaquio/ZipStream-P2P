using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ZipP2P.App.ShellIntegration;

/// <summary>
/// Windows Explorer: verbo em <c>HKCU\Software\Classes\*\shell</c> (arquivos) e
/// <c>...\Directory\shell</c> (pastas). Não exige administrador. No Windows 11 o item
/// aparece em "Mostrar mais opções" (o menu compacto exige um app empacotado/MSIX).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellIntegration : IShellIntegration
{
    public const string VerbName = "ZipStream.Send";

    private readonly string _classesRoot;

    /// <param name="classesRoot">Raiz sob HKCU (troca-se nos testes).</param>
    public WindowsShellIntegration(string classesRoot = @"Software\Classes")
    {
        _classesRoot = classesRoot;
    }

    public string TargetDescription => "Windows Explorer";

    private IEnumerable<string> VerbKeys => new[]
    {
        $@"{_classesRoot}\*\shell\{VerbName}",
        $@"{_classesRoot}\Directory\shell\{VerbName}",
    };

    public static string BuildCommandLine(LaunchCommand command)
        => string.Join(' ', new[] { command.Executable }.Concat(command.ArgumentsFor("%1")).Select(Quote));

    public ShellIntegrationStatus GetStatus(LaunchCommand command)
    {
        var expected = BuildCommandLine(command);
        var installed = VerbKeys
            .Select(key => Registry.CurrentUser.OpenSubKey(key + @"\command")?.GetValue(null) as string)
            .ToList();

        if (installed.All(c => c is null))
            return ShellIntegrationStatus.NotInstalled;

        return installed.All(c => c == expected) ? ShellIntegrationStatus.Installed : ShellIntegrationStatus.Outdated;
    }

    public void Install(LaunchCommand command, string label)
    {
        var commandLine = BuildCommandLine(command);
        foreach (var keyPath in VerbKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(null, label);
            key.SetValue("Icon", $"\"{command.Executable}\",0");
            using var commandKey = key.CreateSubKey("command");
            commandKey.SetValue(null, commandLine);
        }
    }

    public void Uninstall()
    {
        foreach (var keyPath in VerbKeys)
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
    }

    private static string Quote(string arg) => "\"" + arg.Replace("\"", "\\\"") + "\"";
}
