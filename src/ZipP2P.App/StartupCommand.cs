namespace ZipP2P.App;

/// <summary>
/// Ação pedida na linha de comando:
/// <c>--send &lt;caminho&gt;</c> (usado pelo menu de contexto do sistema), <c>--receive &lt;código&gt;</c>,
/// <c>--close-for-update</c> (usado pelo instalador) ou apenas um caminho (ex.: arquivo arrastado sobre
/// o executável / "Abrir com").
/// </summary>
/// <param name="CloseForUpdate">
/// Fecha a instância aberta de verdade (sem ir para a bandeja), para o instalador substituir os arquivos.
/// Se não houver instância aberta, não abre o app.
/// </param>
/// <param name="Background">Abre só na bandeja, sem a janela (<c>--background</c>, usado ao iniciar com o sistema).</param>
public sealed record StartupCommand(string? SendPath, string? ReceiveCode, bool CloseForUpdate = false, bool Background = false)
{
    public const string CloseForUpdateArgument = "--close-for-update";

    public static StartupCommand None { get; } = new(null, null);

    public static StartupCommand CloseForUpdateCommand { get; } = new(null, null, CloseForUpdate: true);

    /// <summary>Resolve caminhos relativos (a outra instância tem outro diretório de trabalho).</summary>
    public StartupCommand WithAbsolutePaths()
        => SendPath is null ? this : this with { SendPath = Path.GetFullPath(SendPath) };

    public static StartupCommand Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case CloseForUpdateArgument:
                    return CloseForUpdateCommand;
                case ShellIntegration.Autostart.BackgroundArgument:
                    return new StartupCommand(null, null, Background: true);
                case "--send" when i + 1 < args.Count:
                    return new StartupCommand(args[i + 1], null);
                case "--receive" when i + 1 < args.Count:
                    return new StartupCommand(null, TransferServices.NormalizeRoomCode(args[i + 1]));
            }

            if (!arg.StartsWith('-') && (File.Exists(arg) || Directory.Exists(arg)))
                return new StartupCommand(arg, null);
        }

        return None;
    }
}
