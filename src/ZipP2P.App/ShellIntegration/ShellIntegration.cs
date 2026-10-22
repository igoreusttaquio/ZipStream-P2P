namespace ZipP2P.App.ShellIntegration;

public enum ShellIntegrationStatus
{
    NotInstalled,
    Installed,

    /// <summary>Instalado, mas apontando para outro executável (app movido/atualizado): reinstalar.</summary>
    Outdated,
}

/// <summary>Comando que o sistema executa para enviar um item: executável + argumentos antes de "--send".</summary>
public sealed record LaunchCommand(string Executable, IReadOnlyList<string> PrefixArguments)
{
    /// <summary>
    /// Comando do processo atual. Se o app roda via "dotnet ZipP2P.App.dll", inclui a dll;
    /// caso contrário, usa o executável (apphost ou bundle .app no macOS).
    /// </summary>
    public static LaunchCommand ForCurrentProcess()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path.");
        var isDotnetHost = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotnetHost && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll)
            return new LaunchCommand(processPath, new[] { dll });

        return new LaunchCommand(processPath, Array.Empty<string>());
    }

    public IEnumerable<string> ArgumentsFor(string path) => PrefixArguments.Concat(new[] { "--send", path });
}

/// <summary>
/// Entrada "Enviar com ZipStream" no menu de contexto do gerenciador de arquivos.
/// No Windows é uma chave no registro do usuário; no Linux/macOS é melhor esforço
/// (depende do gerenciador de arquivos).
/// </summary>
public interface IShellIntegration
{
    /// <summary>Onde a entrada aparece (texto para o usuário).</summary>
    string TargetDescription { get; }

    ShellIntegrationStatus GetStatus(LaunchCommand command);

    void Install(LaunchCommand command, string label);

    void Uninstall();
}

public static class ShellIntegrationFactory
{
    /// <summary>Integração do sistema atual, ou null se não houver suporte.</summary>
    public static IShellIntegration? ForCurrentOs()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsShellIntegration();
        if (OperatingSystem.IsMacOS())
            return new MacShellIntegration(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (OperatingSystem.IsLinux())
            return new LinuxShellIntegration(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return null;
    }
}
