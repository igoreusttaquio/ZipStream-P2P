using System.Globalization;
using ZipP2P.App.Localization;

namespace ZipP2P.App.Devices;

/// <summary>Texto da notificação do sistema para um evento do modo de recebimento (null: não notifica).</summary>
public sealed record ReceiveModeNotification(string Title, string Message)
{
    public static ReceiveModeNotification? For(ReceiveModeEvent e)
    {
        var device = e.DeviceName ?? L("Devices.UnknownDevice");
        return e.Kind switch
        {
            ReceiveModeEventKind.Received => new(
                L("Notify.ReceivedTitle"),
                Format("Notify.ReceivedBody", e.Name, device, Path.GetDirectoryName(e.Path) ?? string.Empty)),
            ReceiveModeEventKind.Paired => new(L("Notify.PairedTitle"), Format("Notify.PairedBody", device)),
            ReceiveModeEventKind.Failed => new(L("Notify.FailedTitle"), e.Error ?? string.Empty),

            // "Recebendo..." fica só na tela: uma notificação por transferência basta.
            _ => null,
        };
    }

    private static string L(string key) => Localizer.Instance[key];

    private static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, L(key), args);
}
