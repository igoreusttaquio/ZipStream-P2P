using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace ZipP2P.App;

/// <summary>Serviços da plataforma (vêm da janela principal) usados pelos view models.</summary>
public static class AppServices
{
    public static IStorageProvider? Storage { get; set; }

    public static IClipboard? Clipboard { get; set; }

    public static ILauncher? Launcher { get; set; }

    /// <summary>Usa os serviços desta janela (a principal pode nunca abrir no envio pelo menu de contexto).</summary>
    public static void Attach(Avalonia.Controls.TopLevel topLevel, bool overwrite)
    {
        if (overwrite || Storage is null)
            Storage = topLevel.StorageProvider;
        if (overwrite || Clipboard is null)
            Clipboard = topLevel.Clipboard;
        if (overwrite || Launcher is null)
            Launcher = topLevel.Launcher;
    }
}
