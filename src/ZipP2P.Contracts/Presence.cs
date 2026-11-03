using System.Security.Cryptography;
using System.Text;

namespace ZipP2P.Contracts;

/// <summary>Para que o convite foi feito: parear pelo código do receptor ou transferir entre pareados.</summary>
public enum InvitationKind
{
    Pairing,
    Transfer
}

/// <summary>Convite entregue a um dispositivo em receive mode: entrar na sala <paramref name="RoomCode"/>.</summary>
public sealed record Invitation(string RoomCode, InvitationKind Kind);

/// <summary>
/// Presença de dispositivos em receive mode. Para se registrar com o id de um dispositivo, o cliente
/// assina um desafio do servidor com a identidade; assim ninguém recebe convites destinados a outro.
/// Compartilhado entre o servidor (que só depende deste projeto) e o app.
/// </summary>
public static class Presence
{
    private static readonly byte[] Label = Encoding.UTF8.GetBytes("ZipStream-Presence-v1");

    /// <summary>Id do dispositivo dono da chave pública (SHA-256 da chave SPKI, em hexadecimal).</summary>
    public static string ComputeDeviceId(ReadOnlySpan<byte> publicKey)
        => Convert.ToHexStringLower(SHA256.HashData(publicKey));

    /// <summary>O que o dispositivo assina para provar a identidade ao servidor.</summary>
    public static byte[] ChallengePayload(ReadOnlySpan<byte> challenge)
    {
        var payload = new byte[Label.Length + challenge.Length];
        Label.CopyTo(payload, 0);
        challenge.CopyTo(payload.AsSpan(Label.Length));
        return payload;
    }

    /// <summary>Confere a assinatura ECDSA P-256 (SHA-256) do desafio feita pela identidade <paramref name="publicKey"/>.</summary>
    public static bool VerifyChallenge(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> signature)
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

        return key.KeySize == 256 && key.VerifyData(ChallengePayload(challenge), signature, HashAlgorithmName.SHA256);
    }
}
