using System.Security.Cryptography;
using System.Text;

namespace ZipP2P.Core.Security;

/// <summary>
/// Acordo de chave efêmero via ECDH (P-256) seguido de HKDF-SHA256,
/// resultando em uma chave AES-256 de sessão. A chave pública é compartilhada
/// (não é segredo); o segredo compartilhado derivado nunca sai deste objeto.
/// </summary>
public sealed class KeyAgreement : IDisposable
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("ZipStream-E2EE-v1");

    private readonly ECDiffieHellman _ecdh;

    public KeyAgreement()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
    }

    /// <summary>Chave pública (formato X.509 SPKI) a ser compartilhada com o par.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Deriva a chave AES-256 compartilhada a partir da chave pública do par.</summary>
    public byte[] DeriveKey(ReadOnlySpan<byte> otherPublicKey)
    {
        using var other = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        other.ImportSubjectPublicKeyInfo(otherPublicKey, out _);

        var sharedSecret = _ecdh.DeriveKeyMaterial(other.PublicKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt: null, info: Info);
    }

    public void Dispose() => _ecdh.Dispose();
}
