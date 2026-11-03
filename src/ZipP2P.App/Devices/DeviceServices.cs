using ZipP2P.App.Logging;

namespace ZipP2P.App.Devices;

/// <summary>Peças do receive mode e do envio para dispositivos, com as configurações atuais (instâncias únicas do app).</summary>
public static class DeviceServices
{
    public static DeviceProfile Profile { get; } = new(new SettingsStore());

    public static TrustedDeviceStore Trusted { get; } = new();

    public static ReceiveModeService ReceiveMode { get; } = new(
        Profile,
        Trusted,
        TransferServices.Store,
        TransferServices.CreatePresence,
        TransferServices.CreateRunner,
        () => new AppSettings(new SettingsStore()).DownloadDirectory,
        AppLog.Instance.CreateLogger("ReceiveMode"));

    public static DeviceConnector Connector { get; } = new(
        Profile,
        Trusted,
        TransferServices.CreatePresence,
        () => TransferServices.CreateRunner(),
        TransferServices.GenerateInviteRoom,
        AppLog.Instance.CreateLogger("Devices"));

    /// <summary>Lista de dispositivos confiáveis mudou (pareamento ou remoção), em qualquer tela.</summary>
    public static event Action? TrustedChanged;

    public static void NotifyTrustedChanged() => TrustedChanged?.Invoke();
}
