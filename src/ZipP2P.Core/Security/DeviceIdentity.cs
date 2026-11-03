using System.Security.Cryptography;

namespace ZipP2P.Core.Security;

/// <summary>
/// Identidade permanente de uma instalação: um par de chaves ECDSA (P-256) que assina as chaves
/// efêmeras de cada sessão. Dispositivos pareados guardam a chave pública um do outro e, assim,
/// se reconhecem sem que alguém precise conferir o código SAS.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;

    private DeviceIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        DeviceId = ComputeDeviceId(PublicKey);
    }

    /// <summary>Chave pública (formato X.509 SPKI), enviada ao par no pareamento.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Identificador derivado da chave pública: ninguém escolhe nem falsifica o id de outro.</summary>
    public string DeviceId { get; }

    public static DeviceIdentity Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Recria a identidade a partir da chave privada exportada por <see cref="ExportPrivateKey"/>.</summary>
    public static DeviceIdentity Import(ReadOnlySpan<byte> pkcs8PrivateKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
            if (key.KeySize != 256)
                throw new CryptographicException("The device identity must be a P-256 key.");
            return new DeviceIdentity(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Chave privada (PKCS#8) para persistir. Nunca sai do dispositivo.</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    /// <summary>Confere uma assinatura feita pela identidade cuja chave pública é <paramref name="publicKey"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
        }
        catch (CryptographicException)
        {
            return false;
        }

        return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    /// <summary>Id do dispositivo dono da <paramref name="publicKey"/> (SHA-256 em hexadecimal).</summary>
    public static string ComputeDeviceId(ReadOnlySpan<byte> publicKey)
        => Convert.ToHexStringLower(SHA256.HashData(publicKey));

    public void Dispose() => _key.Dispose();
}
