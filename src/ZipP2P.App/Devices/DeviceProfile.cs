using System.Security.Cryptography;
using ZipP2P.Core.Security;

namespace ZipP2P.App.Devices;

/// <summary>
/// O que identifica este computador para os outros: a identidade permanente (par de chaves), o
/// nome exibido e o código fixo do receive mode. Tudo é criado no primeiro uso e persistido.
/// </summary>
public sealed class DeviceProfile
{
    private const string IdentityKey = "DeviceIdentity";
    private const string ReceiverCodeKey = "ReceiverCode";
    private const string DeviceNameKey = "DeviceName";

    private readonly SettingsStore _store;

    public DeviceProfile(SettingsStore store)
    {
        _store = store;
    }

    /// <summary>Identidade deste computador; gerada na primeira chamada. O chamador descarta o objeto.</summary>
    public DeviceIdentity LoadIdentity()
    {
        if (_store.Get(IdentityKey) is { Length: > 0 } stored)
            return DeviceIdentity.Import(Unprotect(Convert.FromBase64String(stored)));

        var identity = DeviceIdentity.Create();
        _store.Set(IdentityKey, Convert.ToBase64String(Protect(identity.ExportPrivateKey())));
        return identity;
    }

    /// <summary>Nome mostrado aos dispositivos pareados (padrão: nome do computador).</summary>
    public string DeviceName
    {
        get => _store.Get(DeviceNameKey) is { Length: > 0 } name ? name : Environment.MachineName;
        set => _store.Set(DeviceNameKey, value.Trim());
    }

    /// <summary>Código do receive mode, formatado (<c>K7P-2QX-9MRT-H4WZ</c>); gerado na primeira chamada.</summary>
    public string ReceiverCode
    {
        get
        {
            if (Core.Security.ReceiverCode.TryNormalize(_store.Get(ReceiverCodeKey), out var code))
                return Core.Security.ReceiverCode.Format(code);

            return RegenerateReceiverCode();
        }
    }

    /// <summary>
    /// Troca o código: o anterior deixa de servir para novos pareamentos. Os dispositivos já
    /// pareados continuam confiáveis (eles se reconhecem pela identidade, não pelo código).
    /// </summary>
    public string RegenerateReceiverCode()
    {
        var code = Core.Security.ReceiverCode.Generate();
        _store.Set(ReceiverCodeKey, code);
        return code;
    }

    // A chave privada fica cifrada com a conta do usuário no Windows (DPAPI). Nos outros sistemas
    // não há equivalente embutido; o banco fica na pasta de dados do usuário.
    private static byte[] Protect(byte[] data)
        => OperatingSystem.IsWindows() ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser) : data;

    private static byte[] Unprotect(byte[] data)
        => OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser) : data;
}
