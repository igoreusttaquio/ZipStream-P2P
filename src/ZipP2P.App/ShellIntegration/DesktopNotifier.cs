using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ZipP2P.App.Logging;

namespace ZipP2P.App.ShellIntegration;

/// <summary>
/// Notificação nativa do sistema (ex.: "Arquivo recebido" no modo de recebimento, com a janela fechada).
/// Melhor esforço: se o sistema não tiver suporte, só registra no log.
/// </summary>
public interface IDesktopNotifier
{
    void Show(string title, string message);
}

public static class DesktopNotifier
{
    private static readonly Lazy<IDesktopNotifier> Current = new(Create);

    public static IDesktopNotifier ForCurrentOs => Current.Value;

    private static IDesktopNotifier Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsNotifier();
        if (OperatingSystem.IsMacOS())
            return new ProcessNotifier("osascript", MacArguments);
        return new ProcessNotifier("notify-send", LinuxArguments);
    }

    /// <summary>libnotify (<c>notify-send</c>): presente nos desktops Linux comuns.</summary>
    public static IReadOnlyList<string> LinuxArguments(string title, string message)
        => ["--app-name=ZipStream", "--icon=zipstream", title, message];

    /// <summary>AppleScript <c>display notification</c>; aspas e barras escapadas para a string do script.</summary>
    public static IReadOnlyList<string> MacArguments(string title, string message)
        => ["-e", $"display notification \"{EscapeAppleScript(message)}\" with title \"{EscapeAppleScript(title)}\""];

    private static string EscapeAppleScript(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>Linux/macOS: chama a ferramenta de notificação do sistema.</summary>
internal sealed class ProcessNotifier(string program, Func<string, string, IReadOnlyList<string>> arguments) : IDesktopNotifier
{
    private static readonly ILogger Logger = AppLog.Instance.CreateLogger("Notifications");

    public void Show(string title, string message)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments(title, message))
                info.ArgumentList.Add(argument);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Logger.LogDebug("Native notification unavailable ({Program}): {Message}", program, ex.Message);
        }
    }
}

/// <summary>
/// Windows: balão de um ícone de notificação próprio (<c>Shell_NotifyIcon</c> com <c>NIF_INFO</c>). No
/// Windows 10/11 ele aparece como notificação nativa, com o nome e o ícone do ZipStream. O ícone é
/// adicionado só para mostrar o balão e removido depois. Não exige app empacotado nem o SDK do Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsNotifier : IDesktopNotifier
{
    private static readonly ILogger Logger = AppLog.Instance.CreateLogger("Notifications");

    /// <summary>Tempo que o ícone auxiliar fica na bandeja (a notificação continua na Central de Notificações).</summary>
    private static readonly TimeSpan IconLifetime = TimeSpan.FromSeconds(12);

    private const uint IconId = 0x5A50; // "ZP"
    private readonly object _lock = new();
    private IntPtr _window;
    private int _generation;

    public void Show(string title, string message)
    {
        try
        {
            lock (_lock)
            {
                if (_window == IntPtr.Zero)
                    _window = CreateWindowEx(0, "STATIC", "ZipStreamNotifier", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                var data = NewData();
                data.uFlags = NIF_ICON | NIF_TIP | NIF_INFO;
                data.hIcon = AppIcon.Value;
                data.szTip = "ZipStream";
                data.szInfoTitle = Truncate(title, 63);
                data.szInfo = Truncate(message, 255);
                data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
                data.hBalloonIcon = AppIcon.Value;

                // Adiciona o ícone (ou só atualiza o balão, se ainda estiver lá de uma notificação anterior).
                if (!Shell_NotifyIcon(NIM_MODIFY, ref data) && !Shell_NotifyIcon(NIM_ADD, ref data))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var generation = ++_generation;
                _ = RemoveLaterAsync(generation);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Native notification failed: {Message}", ex.Message);
        }
    }

    private async Task RemoveLaterAsync(int generation)
    {
        await Task.Delay(IconLifetime).ConfigureAwait(false);
        lock (_lock)
        {
            // Outra notificação chegou depois: ela remove o ícone quando for a hora dela.
            if (generation != _generation)
                return;

            var data = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>Ícone do executável (o mesmo do app); sem ele, o ícone padrão de aplicativo.</summary>
    private static readonly Lazy<IntPtr> AppIcon = new(() =>
    {
        var large = new IntPtr[1];
        if (Environment.ProcessPath is { } exe && ExtractIconEx(exe, 0, large, null, 1) > 0 && large[0] != IntPtr.Zero)
            return large[0];
        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    });

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_USER = 0x04, NIIF_LARGE_ICON = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
}
