using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace ZipP2P.App;

/// <summary>Serviços da plataforma (vêm da janela principal) usados pelos view models.</summary>
public static class AppServices
{
    public static IStorageProvider? Storage { get; set; }

    public static IClipboard? Clipboard { get; set; }

    public static ILauncher? Launcher { get; set; }
}
