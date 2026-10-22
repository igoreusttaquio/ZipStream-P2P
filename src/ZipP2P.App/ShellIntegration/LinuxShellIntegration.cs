namespace ZipP2P.App.ShellIntegration;

/// <summary>
/// Linux (melhor esforço): instala a ação para os gerenciadores de arquivos mais comuns:
/// Nautilus/GNOME Files e Caja (scripts), Nemo/Cinnamon (nemo_action) e Dolphin/KDE (service menu).
/// </summary>
public sealed class LinuxShellIntegration : IShellIntegration
{
    private const string Marker = "ZipStream";
    private const string FileStem = "zipstream-send";

    private readonly string _home;

    /// <param name="home">Pasta pessoal (troca-se nos testes).</param>
    public LinuxShellIntegration(string home)
    {
        _home = home;
    }

    public string TargetDescription => "Nautilus, Caja, Nemo, Dolphin";

    /// <summary>Arquivos gerenciados (o nome dos scripts é o próprio texto do menu).</summary>
    private IEnumerable<string> ManagedFiles()
    {
        foreach (var dir in ScriptDirectories())
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (IsOurs(file))
                    yield return file;
            }
        }

        foreach (var file in new[] { NemoActionPath, DolphinKf6Path, DolphinKf5Path })
        {
            if (File.Exists(file))
                yield return file;
        }
    }

    private string NemoActionPath => Path.Combine(_home, ".local/share/nemo/actions", FileStem + ".nemo_action");

    private string DolphinKf6Path => Path.Combine(_home, ".local/share/kio/servicemenus", FileStem + ".desktop");

    private string DolphinKf5Path => Path.Combine(_home, ".local/share/kservices5/ServiceMenus", FileStem + ".desktop");

    private IEnumerable<string> ScriptDirectories() => new[]
    {
        Path.Combine(_home, ".local/share/nautilus/scripts"),
        Path.Combine(_home, ".config/caja/scripts"),
    };

    public ShellIntegrationStatus GetStatus(LaunchCommand command)
    {
        var files = ManagedFiles().ToList();
        if (files.Count == 0)
            return ShellIntegrationStatus.NotInstalled;

        // Scripts e .desktop citam o executável de formas diferentes; basta conter o caminho.
        return files.All(f => File.ReadAllText(f).Contains(command.Executable, StringComparison.Ordinal))
            ? ShellIntegrationStatus.Installed
            : ShellIntegrationStatus.Outdated;
    }

    public void Install(LaunchCommand command, string label)
    {
        Uninstall();

        var safeLabel = string.Concat(label.Where(c => c != '/' && !char.IsControl(c)));
        foreach (var dir in ScriptDirectories())
            WriteExecutable(Path.Combine(dir, safeLabel), BuildScript(command));

        WriteExecutable(NemoActionPath, BuildNemoAction(command, label));
        WriteExecutable(DolphinKf6Path, BuildServiceMenu(command, label));
        WriteExecutable(DolphinKf5Path, BuildServiceMenu(command, label));
    }

    public void Uninstall()
    {
        foreach (var file in ManagedFiles().ToList())
            File.Delete(file);
    }

    /// <summary>Script para Nautilus/Caja: recebem os itens selecionados como argumentos.</summary>
    public static string BuildScript(LaunchCommand command)
    {
        var prefix = string.Join(' ', new[] { command.Executable }.Concat(command.PrefixArguments).Select(QuoteForShell));
        return "#!/bin/sh\n" +
            $"# {Marker}: send the selected file or folder\n" +
            "[ -n \"$1\" ] || exit 0\n" +
            $"exec {prefix} --send \"$(readlink -f \"$1\")\"\n";
    }

    public static string BuildNemoAction(LaunchCommand command, string label) =>
        "[Nemo Action]\n" +
        $"Name={label}\n" +
        $"Comment={Marker}\n" +
        $"Exec={DesktopExec(command, "%F")}\n" +
        "Icon-Name=document-send\n" +
        "Selection=s\n" +
        "Extensions=any;\n";

    public static string BuildServiceMenu(LaunchCommand command, string label) =>
        "[Desktop Entry]\n" +
        "Type=Service\n" +
        "X-KDE-ServiceTypes=KonqPopupMenu/Plugin\n" +
        "MimeType=all/allfiles;inode/directory;application/octet-stream;\n" +
        "Actions=zipstreamSend;\n" +
        $"X-ZipStream={Marker}\n" +
        "\n" +
        "[Desktop Action zipstreamSend]\n" +
        $"Name={label}\n" +
        "Icon=document-send\n" +
        $"Exec={DesktopExec(command, "%f")}\n";

    /// <summary>Linha Exec de .desktop: argumentos entre aspas, com \ " ` $ escapados.</summary>
    private static string DesktopExec(LaunchCommand command, string fieldCode)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
        var args = new[] { command.Executable }.Concat(command.PrefixArguments).Select(Q);
        return string.Join(' ', args) + " --send " + fieldCode;
    }

    private static string QuoteForShell(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static bool IsOurs(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            var buffer = new char[256];
            var read = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Contains("# " + Marker + ":", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteExecutable(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
