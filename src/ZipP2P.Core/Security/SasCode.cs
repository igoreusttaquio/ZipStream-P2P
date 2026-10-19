using System.Security.Cryptography;

namespace ZipP2P.Core.Security;

/// <summary>
/// SAS (Short Authentication String): um código curto (6 dígitos) derivado do segredo
/// compartilhado e das chaves públicas, para detecção de ataque MITM. Se um atacante
/// substituir as chaves públicas, os dois lados calculam códigos diferentes.
/// </summary>
public static class SasCode
{
    public static string Compute(ReadOnlySpan<byte> publicKeyA, ReadOnlySpan<byte> publicKeyB, ReadOnlySpan<byte> sharedKey)
    {
        // Ordena as chaves para que o cálculo seja simétrico nos dois lados.
        var first = publicKeyA.SequenceCompareTo(publicKeyB) <= 0 ? publicKeyA : publicKeyB;
        var second = publicKeyA.SequenceCompareTo(publicKeyB) <= 0 ? publicKeyB : publicKeyA;

        var buffer = new byte[sharedKey.Length + first.Length + second.Length];
        sharedKey.CopyTo(buffer);
        first.CopyTo(buffer.AsSpan(sharedKey.Length));
        second.CopyTo(buffer.AsSpan(sharedKey.Length + first.Length));

        var digest = SHA256.HashData(buffer);

        ulong value = 0;
        for (var i = 0; i < 8; i++)
            value = (value << 8) | digest[i];

        return (value % 1_000_000).ToString("D6");
    }
}
