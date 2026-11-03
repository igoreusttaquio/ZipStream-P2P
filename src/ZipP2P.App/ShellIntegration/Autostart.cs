using System.Security;
using Microsoft.Win32;

namespace ZipP2P.App.ShellIntegration;

/// <summary>
/// "Iniciar com o sistema": abre o app na bandeja ao entrar na conta do usuário (para o receive mode
/// ficar disponível sem ninguém abrir o app). Windows: chave Run do usuário; Linux: XDG autostart;
/// macOS: LaunchAgent. Nada exige administrador.
/// </summary>
public sealed class Autostart
{
    /// <summary>Argumento que abre o app só na bandeja, sem a janela.</summary>
    public const string BackgroundArgument = "--background";

    public const string WindowsValueName = "ZipStream";
    public const string MacAgentLabel = "com.zipstream.app";

    private readonly string _home;
    private readonly string _windowsRunKey;

    /// <param name="homeDirectory">Pasta do usuário (troca-se nos testes).</param>
    /// <param name="windowsRunKey">Chave sob HKCU (troca-se nos testes).</param>
    public Autostart(string homeDirectory, string windowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        _home = homeDirectory;
        _windowsRunKey = windowsRunKey;
    }

    public static Autostart ForCurrentUser() => new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string LinuxDesktopFile => Path.Combine(_home, ".config", "autostart", "zipstream.desktop");

    public string MacAgentFile => Path.Combine(_home, "Library", "LaunchAgents", MacAgentLabel + ".plist");

    public bool IsEnabled
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Registry.CurrentUser.OpenSubKey(_windowsRunKey)?.GetValue(WindowsValueName) is string;
            return File.Exists(OperatingSystem.IsMacOS() ? MacAgentFile : LinuxDesktopFile);
        }
    }

    public void Enable(LaunchCommand command)
    {
        var arguments = command.PrefixArguments.Append(BackgroundArgument).ToList();
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(_windowsRunKey);
            key.SetValue(WindowsValueName, string.Join(' ', new[] { command.Executable }.Concat(arguments).Select(QuoteWindows)));
        }
        else if (OperatingSystem.IsMacOS())
        {
            Write(MacAgentFile, BuildMacAgent(command.Executable, arguments));
        }
        else
        {
            Write(LinuxDesktopFile, BuildLinuxDesktopEntry(command.Executable, arguments));
        }
    }

    public void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(_windowsRunKey, writable: true);
            key?.DeleteValue(WindowsValueName, throwOnMissingValue: false);
        }
        else
        {
            File.Delete(OperatingSystem.IsMacOS() ? MacAgentFile : LinuxDesktopFile);
        }
    }

    public static string BuildLinuxDesktopEntry(string executable, IEnumerable<string> arguments)
        => string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=ZipStream",
            "Icon=zipstream",
            "Exec=" + string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteDesktopExec)),
            "X-GNOME-Autostart-enabled=true",
            "NoDisplay=true",
            string.Empty);

    public static string BuildMacAgent(string executable, IEnumerable<string> arguments)
    {
        var items = string.Concat(new[] { executable }.Concat(arguments)
            .Select(a => $"        <string>{SecurityElement.Escape(a)}</string>\n"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{MacAgentLabel}</string>
                <key>ProgramArguments</key>
                <array>
            {items}    </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>

            """;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string QuoteWindows(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    // Especificação XDG: argumentos com espaço entre aspas duplas, escapando ", `, $ e \.
    private static string QuoteDesktopExec(string value)
    {
        if (!value.Any(c => c is ' ' or '"' or '`' or '$' or '\\'))
            return value;

        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        return $"\"{escaped}\"";
    }
}
