using ZipP2P.Core.Security;

namespace ZipP2P.P2P.Transfer;

/// <summary>Dispositivo com quem o pareamento foi concluído: a identidade dele e o nome que ele informou.</summary>
public sealed record PairedPeer(byte[] IdentityKey, string Name)
{
    public string DeviceId => DeviceIdentity.ComputeDeviceId(IdentityKey);
}
