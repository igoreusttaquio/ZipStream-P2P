namespace ZipP2P.App;

/// <summary>
/// Ação pedida na linha de comando:
/// <c>--send &lt;caminho&gt;</c> (usado pelo menu de contexto do sistema), <c>--receive &lt;código&gt;</c>
/// ou apenas um caminho (ex.: arquivo arrastado sobre o executável / "Abrir com").
/// </summary>
public sealed record StartupCommand(string? SendPath, string? ReceiveCode)
{
    public static StartupCommand None { get; } = new(null, null);

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
