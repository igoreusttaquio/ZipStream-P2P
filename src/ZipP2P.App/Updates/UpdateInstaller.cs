using System.Diagnostics;

namespace ZipP2P.App.Updates;

/// <summary>Executa o instalador baixado e abre páginas da release no navegador.</summary>
public static class UpdateInstaller
{
    /// <summary>
    /// Argumentos do Inno Setup: instala sem perguntas (mostra só o progresso) e, com <c>/relaunch=1</c>,
    /// abre o app de novo no fim (ver packaging/windows/zipstream.iss). O instalador fecha este app com
    /// <c>--close-for-update</c> se ele ainda estiver aberto.
    /// </summary>
    public const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1";

    public static void RunInstaller(string installerPath)
        => Process.Start(new ProcessStartInfo(installerPath, InstallerArguments) { UseShellExecute = false });

    public static void OpenInBrowser(Uri url)
        => Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
}
