using ZipP2P.Core.Security;
using ZipP2P.P2P.Transfer;

namespace ZipP2P.App.Devices;

/// <summary>Quem é aceito do outro lado de uma sessão sem código.</summary>
public static class DeviceTrust
{
    /// <summary>Recebendo (receive mode): qualquer dispositivo da lista de confiáveis.</summary>
    public static SessionAuthentication AnyTrusted(DeviceIdentity identity, TrustedDeviceStore trusted)
        => new(identity, key => trusted.Find(DeviceIdentity.ComputeDeviceId(key)) is { } device
            && device.PublicKey.AsSpan().SequenceEqual(key));

    /// <summary>Enviando: só o dispositivo escolhido como destino (outro pareado não pode atender no lugar dele).</summary>
    public static SessionAuthentication OnlyDevice(DeviceIdentity identity, TrustedDevice target)
        => new(identity, key => key.AsSpan().SequenceEqual(target.PublicKey));
}
